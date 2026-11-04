using LiteDB;
using System.Security.Cryptography;
using System.Text;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const long BLOCK_REWARD_INITIAL = 20_000_000_000;
  const int PERIOD_HALVENING_BLOCK_REWARD = 52500;
  const long SUPPLY_MAX = 2 * BLOCK_REWARD_INITIAL * PERIOD_HALVENING_BLOCK_REWARD;

  DatabaseAccounts Accounts = new();

  PoolTXBToken TXPool;

  byte[] IDToken = new byte[TXOutputTokenAnchor.LENGTH_ID_TOKEN];

  BlockchainBitcoin BlockchainBitcoin;

  bool IsMining;
  long FeePerByte;
  ILiteCollection<BsonDocument> BlocksMinedStored;


  internal BlockchainBToken(
    BlockchainBitcoin blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base("TokenBToken", SIZE_BLOCK_MAX, blockchainParent, semaphoreBlockchain)
  {
    BlockchainBitcoin = blockchainParent;

    BlocksMinedStored = LiteDatabase.GetCollection<BsonDocument>("blocksMined");

    TXPool = new PoolTXBToken(this);

    OnBlockInserted += TXPool.RemoveTXs;
    OnBlockRolledBack += TXPool.ReturnTXs;

    blockchainParent.OnBlockInserted += HandleBlockParentInserted;
    blockchainParent.OnBlockInserted += blockParent =>
    {
      if (!BlockchainBitcoin.IsTXAnchorOpen())
        MineBlock();
    };
    blockchainParent.OnBlockRolledBack += RemoveHeaderPlaceholder;

    Encoding.ASCII.GetBytes("BTK").CopyTo(IDToken, 0);

    if (!Accounts.TryLoad(LiteDatabase))
      BlocksStored.DeleteAll();
  }

  protected override Header CreateHeaderGenesis()
  {
    HeaderBToken header = new(
      headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
      hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
      hashAccounts: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      nonce: 0);

    return header;
  }

  internal override HeaderBToken ParseHeader(byte[] buffer, ref int index)
  {
    return new HeaderBToken(buffer, ref index);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool flagIsCoinbase)
  {
    return new TXBToken(buffer, ref index, flagIsCoinbase);
  }

  protected override void InsertBlock(Block block)
  {
    BlocksStored.DeleteMany(
      Query.LT("_id", block.Header.Height - COUNT_MAX_BLOCKS_STORED + 1));

    if (!Accounts.Exists())
      return;

    Accounts.InsertTXs(block);

    if (!SHA256.HashData(Accounts.Serialize()).IsAllBytesEqual(((HeaderBToken)block.Header).HashAccounts))
      throw new ProtocolException($"Hash of DB not equal to HashAccounts of block {block.Header.Hash.ToHexString()}.");
  }

  protected override void RollBackBlock(Block block)
  {
    if (Accounts.Exists())
      Accounts.RollBackTXs(block);
  }

  protected override bool IsTXOwnedConfirmed(TX tX, Block block)
  {
    if (!Accounts.Exists())
      return false;

    TXBToken tXBToken = (TXBToken)tX;

    Account account = Accounts.LoadAccount(tXBToken.IDAccountSource, heightAccountCreated: 0);

    return account.HeightAccountCreated != tXBToken.HeightAccountCreated || account.Nonce > tXBToken.Nonce;
  }

  internal byte[] GetAccountsSerializedAtBlock(byte[] hashBlock)
  {
    if (ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(hashBlock) && Accounts.Exists())
      return Accounts.Serialize();

    return null;
  }

  internal byte[] GetHashBlockAccountsMissing()
  {
    if (!Accounts.Exists())
      return ChainRoot.HeaderTipBlockchain.Hash;

    return null;
  }

  internal void InstallAccountsAtBlock(byte[] hashBlock, byte[] buffer, int length)
  {
    if (Accounts.Exists()
      || !ChainRoot.TryGetHeader(hashBlock, out Header header)
      || header.Height > ChainRoot.HeaderTipBlockchain.Height)
      return;

    RollBack(heightAfterRollBack: header.Height);

    try
    {
      LiteDatabase.RunTransaction(() => Accounts.Install(buffer, length));
    }
    catch
    {
      Accounts.Drop();
      throw;
    }

    RollBack(heightAfterRollBack: GetHeightBlockStoredOldest());

    if (!SHA256.HashData(Accounts.Serialize())
      .IsAllBytesEqual(((HeaderBToken)ChainRoot.HeaderTipBlockchain).HashAccounts))
    {
      Accounts.Drop();
      UpdateTipBlockchain(ChainRoot);

      throw new ProtocolException($"Hash of DB not equal to HashAccounts of oldest stored block.");
    }

    UpdateTipBlockchain(ChainRoot);

    LoadTXsOwned();
  }

  internal void AddTX(TX tX)
  {
    TXPool.AddTX(tX);
  }

  internal void SubmitTX(byte[] tXRaw)
  {
    int index = 0;

    TX tX = ParseTX(tXRaw, ref index, flagIsCoinbase: false);

    TXPool.AddTX(tX);
    InsertTXOwned(tX);
  }

  internal void LoadTXsOwned()
  {
    if (!Accounts.Exists())
      return;

    foreach (BsonDocument documentTXOwned in TXsOwnedStored.Find(Query.EQ("heightConfirmed", 0)).ToList())
      try
      {
        TXPool.AddTX(ParseTXOwned(documentTXOwned));
      }
      catch (ProtocolException)
      { }
  }

  internal override TX GetTX(byte[] hash)
  {
    if (TXPool.TXsByHash.TryGetValue(hash, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem))
      return tXsByHashItem.tX;

    return base.GetTX(hash);
  }

  internal List<byte[]> GetHashesTXNotInPool(List<byte[]> hashes)
  {
    return hashes.Where(h => !TXPool.TXsByHash.ContainsKey(h)).ToList();
  }

  internal List<TXBToken> GetTXsRanked(HashSet<byte[]> hashes)
  {
    return TXPool.GetTXsRanked().Where(t => hashes.Contains(t.Hash)).ToList();
  }

  internal double GetFeePerByteMin()
  {
    return TXPool.GetFeePerByteMin();
  }

  internal void StartMining(long feePerByte)
  {
    // The user has to define the fee rate at which they want to pay for the anchoring.
    // The GUI could also offer a tool that controls the fee rate automatically,
    // e.g. based on past fee rates or market price arbitrage.

    FeePerByte = feePerByte;
    IsMining = true;

    MineBlock();
  }

  internal void StopMining()
  {
    IsMining = false;
  }

  static long GetBlockReward(int height)
  {
    int countHalvings = height / PERIOD_HALVENING_BLOCK_REWARD;

    if (countHalvings >= 64)
      return 0;

    return BLOCK_REWARD_INITIAL >> countHalvings;
  }

  protected override void VerifyCoinbase(Block block)
  {
    long fee = 0;

    for (int i = 1; i < block.TXs.Count; i += 1)
    {
      if (block.TXs[i].Fee > SUPPLY_MAX - fee)
        throw new ProtocolException($"Fees of block exceed supply max.");

      fee += block.TXs[i].Fee;
    }

    if (GetBlockReward(block.Header.Height) + fee != block.TXs[0].GetValueOutputs())
      throw new ProtocolException($"Output values of coinbase not equal to blockReward plus tx fees.");
  }

  TXBToken CreateTXCoinbase(int height, long fees)
  {
    TXOutputP2PKH outputCoinbase = new(GetBlockReward(height) + fees, Wallet.Hash160PKeyPublic);

    TXBToken tXCoinbase = new()
    {
      HeightAccountCreated = height
    };

    tXCoinbase.TXOutputs.Add(outputCoinbase);

    tXCoinbase.SerializeUnsigned();

    return tXCoinbase;
  }

  protected override void MineBlock()
  {
    if (!IsMining || !Accounts.Exists())
      return;

    if (!BlockchainBitcoin.IsTXAnchorOpen())
      BlocksMinedStored.DeleteAll();

    Block block = new(SizeBlockMax);

    int height = ChainRoot.HeaderTipBlockchain.Height + 1;

    int countBytesTXsMax = block.Buffer.Length
      - HeaderBToken.COUNT_HEADER_BYTES
      - VarInt.GetBytes(int.MaxValue).Length
      - TXBToken.LENGTH_TX_COINBASE;

    block.TXs = TXPool.GetTXs(countBytesTXsMax);

    block.TXs.Insert(0, CreateTXCoinbase(height, block.TXs.Sum(t => t.Fee)));

    block.Header = new HeaderBToken()
    {
      Height = height,
      HashPrevious = ChainRoot.HeaderTipBlockchain.Hash,
      MerkleRoot = block.ComputeMerkleRoot(),
      CountTXs = block.TXs.Count
    };

    ((HeaderBToken)block.Header).HashAccounts = Accounts.ComputeHash(block);

    block.Header.ComputeHash();

    TXOutputTokenAnchor anchorToken = new()
    {
      IDToken = IDToken,
      HashBlockPreviousReferenced = block.Header.HashPrevious,
      HashBlockReferenced = block.Header.Hash
    };

    if (!BlockchainBitcoin.TryCreateTXAnchor(anchorToken, FeePerByte))
      return;

    block.Serialize();

    BlocksMinedStored.Insert(new BsonDocument
    {
      ["_id"] = block.Header.Hash,
      ["blockBytes"] = block.Buffer[..block.LengthDataPayload]
    });
  }

  void HandleBlockParentInserted(Block blockParent)
  {
    bool hasAnchorWinner = blockParent.Header.AnchorsWinner.TryGetValue(IDToken, out TXOutputTokenAnchor anchorWinner);
    bool isPlaceholderNew = false;

    if (hasAnchorWinner)
    {
      Header headerPlaceholderNew = new HeaderBToken(
        headerHash: anchorWinner.HashBlockReferenced,
        hashPrevious: anchorWinner.HashBlockPreviousReferenced,
        merkleRootHash: new byte[Crypto.LENGTH_HASH256],
        hashAccounts: new byte[Crypto.LENGTH_HASH256],
        nonce: 0)
      {
        HeaderParent = blockParent.Header
      };

      Header headerLastInTree = ChainRoot.HeaderRoot;

      if (!headerLastInTree.Hash.IsAllBytesEqual(headerPlaceholderNew.Hash))
        ChainRoot.InsertHeadersInTree([headerPlaceholderNew], GetHeightBlockStoredOldest(), out headerLastInTree);

      isPlaceholderNew = headerLastInTree == headerPlaceholderNew;

      if (headerLastInTree != null && !isPlaceholderNew)
        headerLastInTree.HeaderParent = blockParent.Header;
    }

    bool isBlockParentTip = blockParent.Header.HeaderNext == null;

    if (isBlockParentTip && ChainRoot.HeaderTip.Height - ChainRoot.HeaderTipBlockchain.Height > COUNT_MAX_BLOCKS_STORED)
    {
      Resync(ChainRoot.HeaderTip.Height - COUNT_MAX_BLOCKS_STORED);
      UpdateTipBlockchain(ChainRoot);
    }

    if (!hasAnchorWinner)
      return;

    Block block = null;

    if (BlocksMinedStored.FindById(anchorWinner.HashBlockReferenced) is BsonDocument bsonDocumentBlock)
      block = new(bsonDocumentBlock["blockBytes"].AsBinary);

    BlocksMinedStored.Delete(anchorWinner.HashBlockReferenced);

    if (!ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder, out Chain chain))
      return;

    if (block == null)
    {
      if (!isPlaceholderNew)
        return;

      block = LoadBlock(headerPlaceholder.Height);

      if (block == null)
        return;
    }

    if (!TryParseBlockStored(headerPlaceholder, block))
      return;

    chain.Blocks.Add(headerPlaceholder.Height, block);

    UpdateTipBlockchain(chain);
  }

  void RemoveHeaderPlaceholder(Block blockParent)
  {
    if (!ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder, out Chain chain))
      return;

    RemoveHeader(chain, headerPlaceholder);
  }

  void RemoveHeader(Chain chain, Header header)
  {
    if (chain == ChainRoot)
      RollBack(heightAfterRollBack: header.Height - 1);

    chain.CutOff(header);

    UpdateTipBlockchain(ChainRoot);
  }

  protected override void RollBack(int heightAfterRollBack)
  {
    try
    {
      base.RollBack(heightAfterRollBack);
    }
    catch (Exception exception)
    {
      Console.WriteLine($"Rollback of BToken to height {heightAfterRollBack} failed, resync: {exception}");
      Resync(heightAfterRollBack);
    }
  }

  void Resync(int heightRoot)
  {
    Header headerRoot = ChainRoot.HeaderTip.GetHeaderAncestor(heightRoot);

    Accounts.Drop();
    TXPool.Clear();
    BlocksStored.DeleteAll();

    ChainRoot.ReRoot(headerRoot);
  }

  internal void RollBackUnanchored()
  {
    Header header = ChainRoot.HeaderRoot;

    while (header != null && header.HeaderParent == null)
      header = header.HeaderNext;

    while (header != null && header.HeaderParent != null)
      header = header.HeaderNext;

    if (header != null)
      RemoveHeader(ChainRoot, header);
  }
}
