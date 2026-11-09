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

  const int COUNT_BITS_INDEX_SEGMENT = 11;
  const int COUNT_SEGMENTS = 1 << COUNT_BITS_INDEX_SEGMENT;
  const int DEPTH_MARGIN_INSTALL = 6;
  const int COUNT_MAX_BLOCKS_STORED = COUNT_SEGMENTS + DEPTH_MARGIN_INSTALL;
  internal const int LENGTH_HASHES_SEGMENT = COUNT_SEGMENTS * Crypto.LENGTH_HASH256;

  DatabaseAccounts Accounts = new();
  Header HeaderAccountsReplayed;
  int HeightAccountsInstalled;
  int HeightBlockParentTip;

  Header HeaderTipAccountsServed;
  byte[] HashesSegmentServed;

  DateTime TimeRequestHashesSegment;
  byte[] HashBlockAccountsDownload;
  byte[][] HashesSegmentDownload;
  byte[][] SegmentsDownload;
  Dictionary<byte[], (byte[] hash, DateTime timeRequestLast)> HashesSegmentAwaited = new(new EqualityComparerByteArray());

  PoolTXBToken TXPool;

  byte[] IDToken = new byte[TXOutputTokenAnchor.LENGTH_ID_TOKEN];

  BlockchainBitcoin BlockchainBitcoin;

  bool IsMining;
  long FeePerByte;
  bool IsRoundMiningOpen;
  byte[] HashBlockAwaitedRoundMining;
  ILiteCollection<BsonDocument> BlocksCandidateStored;
  const string NAME_FIELD_HEIGHT = "height";


  internal BlockchainBToken(
    BlockchainBitcoin blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base("TokenBToken", SIZE_BLOCK_MAX, blockchainParent, semaphoreBlockchain)
  {
    BlockchainBitcoin = blockchainParent;

    BlocksCandidateStored = LiteDatabase.GetCollection<BsonDocument>("blocksCandidate");

    TXPool = new PoolTXBToken(this);

    OnBlockInserted += TXPool.RemoveTXs;
    OnBlockRolledBack += TXPool.ReturnTXs;

    blockchainParent.OnBlockInserted += HandleBlockParentInserted;
    blockchainParent.OnBlockRolledBack += RemoveHeaderPlaceholder;

    Encoding.ASCII.GetBytes("BTK").CopyTo(IDToken, 0);

    if (!Accounts.TryLoad(LiteDatabase))
      BlocksStored.DeleteAll();
  }

  protected override Header CreateHeaderRoot()
  {
    HeaderBToken header = new(
      headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
      hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
      hashAccounts: "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d".ToBinary(),
      nonce: 0);

    return header;
  }

  internal override HeaderBToken ParseHeader(byte[] buffer, ref int index)
  {
    return new HeaderBToken(buffer, ref index);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase)
  {
    return new TXBToken(buffer, ref index, isCoinbase);
  }

  protected override void InsertBlock(Block block)
  {
    TXsOwnedStored.DeleteMany(Query.Between(NAME_FIELD_HEIGHT_CONFIRMED, 1, block.Header.Height - COUNT_MAX_BLOCKS_STORED));

    BlocksCandidateStored.Delete(block.Header.Hash);

    if (!IsAccountsAtTip())
      return;

    int heightPrune = block.Header.Height - COUNT_MAX_BLOCKS_STORED + 1;

    BlocksStored.DeleteMany(Query.LT(NAME_FIELD_ID, heightPrune));
    BlocksCandidateStored.DeleteMany(Query.LT(NAME_FIELD_HEIGHT, heightPrune));

    Accounts.InsertBlock(block);
  }

  protected override void RollBackBlock(Block block)
  {
    StoreBlockCandidate(block);

    if (IsAccountsAtTip())
      Accounts.RollBack(block.Header.Height);
  }

  bool IsAccountsAtTip()
  {
    return Accounts.Exists() && HeaderAccountsReplayed == null;
  }

  protected override bool IsTXOwnedConfirmed(TX tX, Block block)
  {
    if (!IsAccountsAtTip())
      return false;

    TXBToken tXBToken = (TXBToken)tX;

    Account account = Accounts.LoadAccount(tXBToken.IDAccountSource, heightAccountCreated: 0);

    return account.HeightAccountCreated != tXBToken.HeightAccountCreated || account.Nonce > tXBToken.Nonce;
  }

  bool IsWindowStoredForAccountsExchange()
  {
    return GetHeightBlockStoredOldest() <= ChainRoot.HeaderTipBlockchain.Height - COUNT_MAX_BLOCKS_STORED + 1;
  }

  internal byte[] GetHashesSegmentAtBlock(byte[] hashBlock)
  {
    Header headerTip = ChainRoot.HeaderTipBlockchain;

    if (!headerTip.Hash.IsAllBytesEqual(hashBlock) || !IsAccountsAtTip() || !IsWindowStoredForAccountsExchange())
      return null;

    if (HeaderTipAccountsServed != headerTip)
    {
      List<byte> hashesSegment = new();

      for (int i = 0; i < COUNT_SEGMENTS; i += 1)
        hashesSegment.AddRange(SHA256.HashData(Accounts.SerializeSegmentBelowWindow(i)));

      HashesSegmentServed = hashesSegment.ToArray();
      HeaderTipAccountsServed = headerTip;
    }

    return HashesSegmentServed;
  }

  internal byte[] GetSegmentServed(byte[] hashSegment)
  {
    if (HashesSegmentServed == null || !IsAccountsAtTip() || !IsWindowStoredForAccountsExchange())
      return null;

    for (int i = 0; i < COUNT_SEGMENTS; i += 1)
      if (HashesSegmentServed.AsSpan(i * Crypto.LENGTH_HASH256, Crypto.LENGTH_HASH256).SequenceEqual(hashSegment))
      {
        byte[] segment = Accounts.SerializeSegmentBelowWindow(i);

        if (SHA256.HashData(segment).IsAllBytesEqual(hashSegment))
          return segment;
      }

    return null;
  }

  internal byte[] FetchHashBlockAccountsMissing(Func<byte[], bool> isPeerPicked)
  {
    byte[] hashTip = ChainRoot.HeaderTipBlockchain.Hash;

    if (Accounts.Exists()
      || !IsWindowStoredForAccountsExchange()
      || hashTip.IsAllBytesEqual(HashBlockAccountsDownload)
      || DateTime.UtcNow - TimeRequestHashesSegment < TimeSpan.FromSeconds(Network.TIMEOUT_REQUEST_SECONDS)
      || !isPeerPicked(hashTip))
      return null;

    TimeRequestHashesSegment = DateTime.UtcNow;

    return hashTip;
  }

  internal void EndRequestHashesSegment()
  {
    TimeRequestHashesSegment = DateTime.MinValue;
  }

  internal byte[] FetchHashSegmentMissingNext(Func<byte[], bool> canSupplierDeliverSegment, byte[] hashBlockAccountsRefused)
  {
    if (HashesSegmentDownload == null || hashBlockAccountsRefused?.IsAllBytesEqual(HashBlockAccountsDownload) == true)
      return null;

    return FetchItemMissingNext(
      HashesSegmentDownload.Where((h, i) => SegmentsDownload[i] == null),
      HashesSegmentAwaited,
      h => h,
      canSupplierDeliverSegment);
  }

  internal void SetHashesSegmentDownload(byte[] hashBlock, byte[] hashesSegment)
  {
    TimeRequestHashesSegment = DateTime.MinValue;

    if (Accounts.Exists())
      return;

    byte[][] hashesSegmentNew = hashesSegment.Chunk(Crypto.LENGTH_HASH256).ToArray();
    byte[][] segmentsKept = AdvanceSegmentsDownload(hashBlock) ?? new byte[COUNT_SEGMENTS][];

    SegmentsDownload = new byte[COUNT_SEGMENTS][];

    for (int i = 0; i < COUNT_SEGMENTS; i += 1)
      if (segmentsKept[i] != null && SHA256.HashData(segmentsKept[i]).IsAllBytesEqual(hashesSegmentNew[i]))
        SegmentsDownload[i] = segmentsKept[i];

    HashBlockAccountsDownload = hashBlock;
    HashesSegmentDownload = hashesSegmentNew;
    HashesSegmentAwaited.Clear();
  }

  byte[][] AdvanceSegmentsDownload(byte[] hashBlockNew)
  {
    if (SegmentsDownload == null
      || !ChainRoot.TryGetHeader(HashBlockAccountsDownload, out Header headerTipOld)
      || !ChainRoot.TryGetHeader(hashBlockNew, out Header headerTipNew)
      || headerTipNew.GetHeaderAncestor(headerTipOld.Height) != headerTipOld)
      return null;

    List<Block> blocks = new();

    for (
      Header header = headerTipNew.GetHeaderAncestor(headerTipNew.Height - COUNT_MAX_BLOCKS_STORED);
      header.Height > headerTipOld.Height - COUNT_MAX_BLOCKS_STORED;
      header = header.HeaderPrevious)
      blocks.Insert(0, LoadBlockParsed(header));

    if (blocks.Count == 0)
      return SegmentsDownload;

    try
    {
      return Accounts.AdvanceSegments(SegmentsDownload, blocks);
    }
    catch (ProtocolException)
    {
      return null;
    }
  }

  internal void AddSegmentDownload(byte[] hashSegment, byte[] buffer, int length)
  {
    if (HashesSegmentDownload == null)
      return;

    byte[] segment = buffer[..length];

    for (int i = 0; i < COUNT_SEGMENTS; i += 1)
      if (HashesSegmentDownload[i].IsAllBytesEqual(hashSegment))
      {
        if (!DatabaseAccounts.IsSegmentValid(segment, i))
        {
          HashesSegmentDownload = null;
          SegmentsDownload = null;
          return;
        }

        SegmentsDownload[i] = segment;
      }

    if (SegmentsDownload.Contains(null))
      return;

    try
    {
      InstallAccountsAtBlock(HashBlockAccountsDownload, SegmentsDownload.ToList());
    }
    finally
    {
      HashBlockAccountsDownload = null;
      HashesSegmentDownload = null;
      SegmentsDownload = null;
    }
  }

  void InstallAccountsAtBlock(byte[] hashBlock, List<byte[]> segments)
  {
    if (Accounts.Exists()
      || !ChainRoot.TryGetHeader(hashBlock, out Header headerRequested)
      || headerRequested.Height > ChainRoot.HeaderTipBlockchain.Height
      || headerRequested.GetHeaderAncestor(headerRequested.Height - COUNT_MAX_BLOCKS_STORED) is not Header header)
      return;

    try
    {
      LiteDatabase.RunTransaction(() => Accounts.Install(segments));
    }
    catch
    {
      Accounts.Drop();
      throw;
    }

    HeaderAccountsReplayed = header;
    HeightAccountsInstalled = header.Height;

    Task.Run(RunFullCheck);
  }

  async Task RunFullCheck()
  {
    while (await RunLocked(ReplayBlockNext))
    { }
  }

  bool ReplayBlockNext()
  {
    if (HeaderAccountsReplayed == null)
      return false;

    Header headerTip = ChainRoot.HeaderTipBlockchain;

    try
    {
      if (HeaderAccountsReplayed == headerTip
        || headerTip.GetHeaderAncestor(HeaderAccountsReplayed.Height) != HeaderAccountsReplayed)
        throw new ProtocolException($"A rollback reached the replayed block {HeaderAccountsReplayed.Hash.ToHexString()}.");

      Header header = HeaderAccountsReplayed.HeaderNext;
      Block block = LoadBlockParsed(header);

      try
      {
        LiteDatabase.RunTransaction(() =>
        {
          Accounts.InsertBlock(block);

          if (header == headerTip)
            Accounts.MarkComplete();
        });

        HeaderAccountsReplayed = header;

        if (header != headerTip)
          return true;
      }
      catch (ProtocolException exception) when (header.Height - HeightAccountsInstalled > COUNT_SEGMENTS)
      {
        RejectBlock(block, exception);
        Accounts.MarkComplete();
      }
    }
    catch (Exception exception)
    {
      Console.WriteLine($"Full check of the account database failed, the database is downloaded again: {exception.Message}");

      Accounts.Drop();
      HeaderAccountsReplayed = null;
      return false;
    }

    HeaderAccountsReplayed = null;

    UpdateTipBlockchain(ChainRoot);

    LoadTXsOwnedUnconfirmed();

    return false;
  }

  internal void AddTX(TX tX)
  {
    TXPool.AddTX(tX);
  }

  internal void SubmitTX(byte[] tXRaw)
  {
    int index = 0;

    TX tX = ParseTX(tXRaw, ref index, isCoinbase: false);

    TXPool.AddTX(tX);
    InsertTXOwned(tX);
  }

  internal void Load()
  {
    LoadBlockchain();
    BlockchainBitcoin.LoadBlockchain();
    RollBackUnanchored();
    LoadTXsOwnedUnconfirmed();
  }

  void LoadTXsOwnedUnconfirmed()
  {
    if (!IsAccountsAtTip())
      return;

    foreach (BsonDocument documentTXOwned in TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
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
    if (!IsMining || !IsAccountsAtTip())
      return;

    bool isTipAwaited = HashBlockAwaitedRoundMining != null
      && ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(HashBlockAwaitedRoundMining);

    if (!IsRoundMiningOpen && !isTipAwaited)
      return;

    IsRoundMiningOpen = false;

    if (isTipAwaited)
      HashBlockAwaitedRoundMining = null;

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
      MerkleRoot = block.ComputeMerkleRoot()
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

    StoreBlockCandidate(block);
  }

  void StoreBlockCandidate(Block block)
  {
    BlocksCandidateStored.Upsert(new BsonDocument
    {
      [NAME_FIELD_ID] = block.Header.Hash,
      [NAME_FIELD_HEIGHT] = block.Header.Height,
      [NAME_FIELD_BLOCK_BYTES] = SerializeBlockStored(block)
    });
  }

  void HandleBlockParentInserted(Block blockParent)
  {
    bool hasAnchorWinner = blockParent.Header.AnchorsWinner.TryGetValue(IDToken, out TXOutputTokenAnchor anchorWinner);
    bool isBlockParentTip = blockParent.Header.HeaderNext == null;

    if (isBlockParentTip)
    {
      IsRoundMiningOpen = true;
      HashBlockAwaitedRoundMining = hasAnchorWinner ? anchorWinner.HashBlockReferenced : null;
    }

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

      Header headerLastInTree = ChainRoot.FindInTree(
        chain => chain.TryGetHeader(headerPlaceholderNew.Hash, out Header header) ? header : null);

      if (headerLastInTree == null)
        ChainRoot.InsertHeadersInTree([headerPlaceholderNew], GetHeightBlockStoredOldest(), out headerLastInTree);
      else if (!headerLastInTree.HashPrevious.IsAllBytesEqual(headerPlaceholderNew.HashPrevious))
        headerLastInTree = null;

      if (headerLastInTree != null && headerLastInTree != headerPlaceholderNew && headerLastInTree.HeaderParent == null)
        headerLastInTree.HeaderParent = blockParent.Header;
    }

    if (isBlockParentTip)
    {
      Chain chainTallest = ChainRoot.FindChainTallest();

      if (CountPlaceholdersCatchUp(chainTallest.HeaderTip) > COUNT_MAX_BLOCKS_STORED)
      {
        Resync(chainTallest);
        UpdateTipBlockchain(ChainRoot);
      }

      HeightBlockParentTip = blockParent.Header.Height;
    }

    if (hasAnchorWinner
      && ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder, out Chain chain)
      && BlocksCandidateStored.FindById(anchorWinner.HashBlockReferenced) is BsonDocument bsonDocumentBlock)
    {
      Block block = new(bsonDocumentBlock[NAME_FIELD_BLOCK_BYTES].AsBinary);

      if (TryParseBlockStored(headerPlaceholder, block) && chain.BlocksQueued.TryAdd(headerPlaceholder.Height, block))
        UpdateTipBlockchain(chain);
    }

    MineBlock();
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

  int CountPlaceholdersCatchUp(Header headerTip)
  {
    int countPlaceholdersCatchUp = 0;

    for (
      Header header = headerTip;
      header != ChainRoot.HeaderTipBlockchain.GetHeaderAncestor(header.Height);
      header = header.HeaderPrevious)
      if (header.HeaderParent.Height > HeightBlockParentTip)
        countPlaceholdersCatchUp += 1;

    return countPlaceholdersCatchUp;
  }

  void Resync(Chain chainTallest)
  {
    Accounts.Drop();
    HeaderAccountsReplayed = null;
    TXPool.Clear();
    BlocksStored.DeleteAll();

    ChainRoot.HeaderTipBlockchain = ChainRoot.HeaderRoot;
    Reorg(chainTallest);

    Header headerRoot = ChainRoot.HeaderTip.GetHeaderAncestor(ChainRoot.HeaderTip.Height - COUNT_MAX_BLOCKS_STORED);

    ChainRoot.ReRoot(headerRoot);
  }

  void RollBackUnanchored()
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
