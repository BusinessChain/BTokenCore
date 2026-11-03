using LiteDB;
using System.Security.Cryptography;
using System.Text;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  internal DatabaseAccounts Accounts = new();

  PoolTXBToken TXPool;

  internal byte[] IDToken = new byte[TXOutputTokenAnchor.LENGTH_ID_TOKEN];

  BlockchainBitcoin BlockchainBitcoin;

  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();
  ILiteCollection<BsonDocument> DatabaseBlocksMinedCollection;


  internal BlockchainBToken(
    BlockchainBitcoin blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base("TokenBToken", blockchainParent, semaphoreBlockchain)
  {
    SizeBlockMax = SIZE_BLOCK_MAX;

    BlockchainBitcoin = blockchainParent;

    DatabaseBlocksMinedCollection = Database.GetCollection<BsonDocument>("blocksMined");

    TXPool = new PoolTXBToken(this);

    OnBlockInserted += TXPool.RemoveTXs;
    OnBlockRolledBack += TXPool.ReturnTXs;

    blockchainParent.OnBlockInserted += HandleBlockParentInserted;
    blockchainParent.OnBlockRolledBack += RemoveHeaderPlaceholder;

    Encoding.ASCII.GetBytes("BTK").CopyTo(IDToken, 0);

    if (!Accounts.TryLoad(Database))
      DatabaseBlockCollection.DeleteAll();
  }

  protected override Header CreateHeaderGenesis()
  {
    HeaderBToken header = new(
      headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
      hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
      hashDatabase: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
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
    DatabaseBlockCollection.DeleteMany(
      Query.LT("_id", block.Header.Height - MAX_COUNT_BLOCKS_STORED + 1));

    if (!Accounts.Exists())
      return;

    InsertTXs(block);

    if (!SHA256.HashData(Accounts.Serialize()).IsAllBytesEqual(((HeaderBToken)block.Header).HashDatabase))
      throw new ProtocolException($"Hash of DB not equal to HashDatabase of block {block.Header.Hash.ToHexString()}.");
  }

  void InsertTXs(Block block)
  {
    for (int i = 0; i < block.TXs.Count; i += 1)
    {
      TXBToken tX = (TXBToken)block.TXs[i];

      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput.Value > 0)
          AddToBalance(tXOutput.IDAccount, block.Header.Height, tXOutput.Value);

      if (i > 0)
        SpendTXInput(tX);
    }
  }

  void AddToBalance(byte[] accountID, int blockHeightAccountCreated, long value)
  {
    Account account = Accounts.LoadAccount(accountID, blockHeightAccountCreated);
    account.Balance += value;
    Accounts.WriteAccount(account);
  }

  void SpendTXInput(TXBToken tX)
  {
    Account account = Accounts.GetCopyOfAccount(tX.IDAccountSource);
    account.SpendTX(tX);
    Accounts.WriteAccount(account);
  }

  protected override void RollBackBlock(Block block)
  {
    if (!Accounts.Exists())
      return;

    for (int i = block.TXs.Count - 1; i >= 0; i--)
    {
      TXBToken tX = block.TXs[i] as TXBToken;

      if (i > 0)
      {
        Account accountSource = Accounts.LoadAccount(tX.IDAccountSource, tX.BlockheightAccountCreated);
        accountSource.ReverseSpendTX(tX);
        Accounts.WriteAccount(accountSource);
      }

      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput.Value > 0)
          AddToBalance(tXOutput.IDAccount, block.Header.Height, -tXOutput.Value);
    }
  }

  protected override bool IsTXOwnedConfirmed(TX tX, Block block)
  {
    if (!Accounts.Exists())
      return false;

    TXBToken tXBToken = (TXBToken)tX;

    Account account = Accounts.LoadAccount(tXBToken.IDAccountSource, blockHeightAccountCreated: 0);

    return account.BlockHeightAccountCreated != tXBToken.BlockheightAccountCreated || account.Nonce > tXBToken.Nonce;
  }

  internal byte[] GetDB(byte[] hash)
  {
    if (ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(hash) && Accounts.Exists())
      return Accounts.Serialize();

    return null;
  }

  internal byte[] GetHashDBMissing()
  {
    if (!Accounts.Exists())
      return ChainRoot.HeaderTipBlockchain.Hash;

    return null;
  }

  internal void InstallDB(byte[] hash, byte[] buffer, int length)
  {
    if (Accounts.Exists()
      || !ChainRoot.TryGetHeader(hash, out Header header)
      || header.Height > ChainRoot.HeaderTipBlockchain.Height)
      return;

    RollBack(heightAfterRollBack: header.Height);

    try
    {
      RunInDatabaseTransaction(() => Accounts.Install(buffer, length));
    }
    catch
    {
      Accounts.Drop();
      throw;
    }

    RollBack(heightAfterRollBack: GetHeightBlockStoredOldest());

    if (!SHA256.HashData(Accounts.Serialize())
      .IsAllBytesEqual(((HeaderBToken)ChainRoot.HeaderTipBlockchain).HashDatabase))
    {
      Accounts.Drop();
      UpdateTipBlockchain(ChainRoot);

      throw new ProtocolException($"Hash of DB not equal to HashDatabase of oldest stored block.");
    }

    UpdateTipBlockchain(ChainRoot);
  }

  internal void AddTX(TX tX)
  {
    TXPool.AddTX(tX);
  }

  internal override TX GetTX(byte[] hash)
  {
    TXPool.TXsByHash.TryGetValue(hash, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem);

    return tXsByHashItem.tX;
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

    OnTipChanged();
  }

  internal void StopMining()
  {
    IsMining = false;
  }

  protected override void OnTipChanged()
  {
    if (!IsMining)
      return;

    if (!BlockchainBitcoin.IsTXAnchorOpen())
    {
      BlocksMinedCache.Clear();
      DatabaseBlocksMinedCollection.DeleteAll();
    }

    Block block = new(SizeBlockMax);

    int height = ChainRoot.HeaderTipBlockchain.Height + 1;

    block.TXs = TXPool.GetTXs(block.Buffer.Length);

    long feeTXs = block.TXs.Sum(t => t.Fee);

    long blockReward = HeaderBToken.GetBlockReward(height) + feeTXs;

    TX tXCoinbase = CreateTXCoinbase(height, blockReward, Wallet.Hash160PKeyPublic);

    block.TXs.Insert(0, tXCoinbase);

    block.Header = new HeaderBToken()
    {
      Height = height,
      HashPrevious = ChainRoot.HeaderTipBlockchain.Hash,
      MerkleRoot = block.ComputeMerkleRoot(),
      CountTXs = block.TXs.Count,
      Fee = feeTXs
    };

    Database.BeginTrans();

    try
    {
      InsertTXs(block);
      ((HeaderBToken)block.Header).HashDatabase = SHA256.HashData(Accounts.Serialize());
    }
    finally
    {
      Database.Rollback();
    }

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

    BlocksMinedCache.Add(block);

    DatabaseBlocksMinedCollection.Insert(new BsonDocument
    {
      ["_id"] = block.Header.Hash,
      ["blockBytes"] = block.Buffer[..block.LengthDataPayload]
    });
  }

  TX CreateTXCoinbase(int blockHeight, long blockReward, byte[] hash160PKeyPublic)
  {
    TXBToken tX = new()
    {
      BlockheightAccountCreated = blockHeight,
    };

    tX.TXOutputs.Add(new TXOutputP2PKH(blockReward, hash160PKeyPublic));

    tX.SerializeUnsigned();

    return tX;
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
        merkleRootHash: new byte[32],
        hashDatabase: new byte[32],
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

    if (isBlockParentTip && ChainRoot.HeaderTip.Height - ChainRoot.HeaderTipBlockchain.Height > MAX_COUNT_BLOCKS_STORED)
    {
      Accounts.Drop();
      DatabaseBlockCollection.DeleteAll();

      Header headerRoot = ChainRoot.HeaderTip;

      while (headerRoot.Height > ChainRoot.HeaderTip.Height - MAX_COUNT_BLOCKS_STORED)
        headerRoot = headerRoot.HeaderPrevious;

      ChainRoot.ReRoot(headerRoot);

      UpdateTipBlockchain(ChainRoot);
    }

    if (!hasAnchorWinner)
      return;

    Block block = BlocksMinedCache
      .Find(b => b.Header.Hash.IsAllBytesEqual(anchorWinner.HashBlockReferenced));

    if (block != null)
      BlocksMinedCache.Remove(block);
    else if (DatabaseBlocksMinedCollection.FindById(anchorWinner.HashBlockReferenced) is BsonDocument bsonDocumentBlock)
      block = new(bsonDocumentBlock["blockBytes"].AsBinary);

    DatabaseBlocksMinedCollection.Delete(anchorWinner.HashBlockReferenced);

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
