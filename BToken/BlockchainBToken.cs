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
  const int DEPTH_MARGIN_RESYNC = 6;

  DatabaseAccounts Accounts = new();
  Header HeaderAccountsReplayed;

  byte[][] SegmentsDownload = new byte[COUNT_SEGMENTS][];
  Header[] HeadersSweepDownload = new Header[COUNT_SEGMENTS];
  Dictionary<byte[], (Header header, DateTime timeRequestLast)> HeadersSweepAwaited = new(new EqualityComparerByteArray());

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

    Accounts.Load(LiteDatabase);
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

  internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures)
  {
    return new TXBToken(buffer, ref index, isCoinbase, verifySignatures);
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

    Header header = block.Header;

    if (!Accounts.Exists() || HeaderAccountsReplayed != null && header.Height > HeaderAccountsReplayed.Height)
      return;

    if (header.Height <= Accounts.GetHeightInstalled())
    {
      if (HeaderAccountsReplayed == null)
        Task.Run(RunFullCheck);

      HeaderAccountsReplayed = header;
      return;
    }

    Accounts.RollBack(header);

    if (HeaderAccountsReplayed != null)
      HeaderAccountsReplayed = header.HeaderPrevious;
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

  internal byte[] GetSegmentAtSweep(byte[] hashBlock)
  {
    int heightTip = ChainRoot.HeaderTipBlockchain.Height;

    if (!IsAccountsAtTip()
      || !ChainRoot.TryGetHeader(hashBlock, out Header header)
      || header.Height > heightTip
      || header.Height < heightTip - COUNT_MAX_BLOCKS_STORED
      || header.Height < Accounts.GetHeightInstalled())
      return null;

    return Accounts.SerializeSegmentAtHeight(header.Height % COUNT_SEGMENTS, header.Height);
  }

  void DiscardSegmentsOffMainChain()
  {
    Header headerTip = ChainRoot.HeaderTipBlockchain;

    for (int i = 0; i < COUNT_SEGMENTS; i += 1)
      if (HeadersSweepDownload[i] is Header header
        && (header.Height > headerTip.Height || headerTip.GetHeaderAncestor(header.Height) != header))
      {
        HeadersSweepDownload[i] = null;
        SegmentsDownload[i] = null;
      }
  }

  internal Header FetchHeaderSweepMissingNext(Func<Header, bool> canSupplierDeliver)
  {
    if (Accounts.Exists() || !IsWindowStoredForAccountsExchange())
      return null;

    DiscardSegmentsOffMainChain();

    return FetchItemMissingNext(
      EnumerateHeadersSweepMissing(),
      HeadersSweepAwaited,
      h => h.Hash,
      canSupplierDeliver);
  }

  IEnumerable<Header> EnumerateHeadersSweepMissing()
  {
    Header header = ChainRoot.HeaderTipBlockchain.GetHeaderAncestor(ChainRoot.HeaderTipBlockchain.Height - DEPTH_MARGIN_INSTALL);

    for (int i = 0; i < COUNT_SEGMENTS; i += 1, header = header.HeaderPrevious)
      if (SegmentsDownload[header.Height % COUNT_SEGMENTS] == null)
        yield return header;
  }

  internal bool AddSegmentDownload(byte[] hashBlock, byte[] buffer, int length)
  {
    if (Accounts.Exists()
      || !ChainRoot.TryGetHeader(hashBlock, out Header header)
      || header.Height > ChainRoot.HeaderTipBlockchain.Height)
      return true;

    byte[] segment = buffer[..length];

    if (!SHA256.HashData(segment).IsAllBytesEqual(((HeaderBToken)header).HashAccounts))
      return false;

    int indexSegment = header.Height % COUNT_SEGMENTS;

    if (!DatabaseAccounts.IsSegmentValid(segment, indexSegment))
    {
      RejectBlock(header, new ProtocolException($"Segment {indexSegment} committed by block {header.Hash.ToHexString()} is invalid."));
      UpdateTipBlockchain(ChainRoot);
      return true;
    }

    SegmentsDownload[indexSegment] = segment;
    HeadersSweepDownload[indexSegment] = header;

    TryInstallSweeps();

    return true;
  }

  void TryInstallSweeps()
  {
    if (Accounts.Exists())
      return;

    DiscardSegmentsOffMainChain();

    Header headerTip = ChainRoot.HeaderTipBlockchain;
    int heightInstall = headerTip.Height - DEPTH_MARGIN_INSTALL;

    if (SegmentsDownload.Contains(null) || HeadersSweepDownload.Any(h => h.Height > heightInstall))
      return;

    InstallAccounts(
      headerTip.GetHeaderAncestor(heightInstall),
      SegmentsDownload,
      HeadersSweepDownload.Select(h => h.Height).ToArray());
  }

  void InstallAccounts(Header header, IList<byte[]> segments, int[] heightsSweep)
  {
    List<Header> headersCarried = new();

    for (Header headerCarried = header; headerCarried.Height > heightsSweep.DefaultIfEmpty(header.Height).Min(); headerCarried = headerCarried.HeaderPrevious)
      headersCarried.Insert(0, headerCarried);

    Header headerFailed = null;

    try
    {
      LiteDatabase.RunTransaction(() =>
      {
        Accounts.Install(segments, header);

        foreach (Header headerCarried in headersCarried)
        {
          headerFailed = headerCarried;
          Accounts.CarryBlock(LoadBlockParsed(headerCarried), heightsSweep);
        }
      });
    }
    catch (ProtocolException exception) when (headerFailed != null)
    {
      Accounts.Drop();
      RejectBlock(headerFailed, exception);
      UpdateTipBlockchain(ChainRoot);
      return;
    }
    catch
    {
      Accounts.Drop();
      throw;
    }

    Array.Clear(SegmentsDownload);
    Array.Clear(HeadersSweepDownload);
    HeadersSweepAwaited.Clear();

    HeaderAccountsReplayed = header;

    Task.Run(RunFullCheck);
  }

  void ResolveNodeWithoutState()
  {
    ResyncWhenBehind();

    if (!Accounts.Exists() && ChainRoot.HeaderRoot.Height == 0)
      InstallAccounts(ChainRoot.HeaderRoot, [], []);
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
      if (headerTip.GetHeaderAncestor(HeaderAccountsReplayed.Height) != HeaderAccountsReplayed)
        throw new ProtocolException($"A rollback reached the replayed block {HeaderAccountsReplayed.Hash.ToHexString()}.");

      if (HeaderAccountsReplayed != headerTip)
      {
        Header header = HeaderAccountsReplayed.HeaderNext;
        Block block = LoadBlockParsed(header);

        try
        {
          LiteDatabase.RunTransaction(() => Accounts.InsertBlock(block));

          HeaderAccountsReplayed = header;

          if (header != headerTip)
            return true;
        }
        catch (ProtocolException exception)
        {
          RejectBlock(header, exception);
        }
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

    TX tX = ParseTX(tXRaw, ref index, isCoinbase: false, verifySignatures: true);

    TXPool.AddTX(tX);
    InsertTXOwned(tX);
  }

  internal void Load()
  {
    LoadBlockchain();

    if (Accounts.Exists())
      if (ChainRoot.TryGetHeader(Accounts.GetHashBlockAppliedLast(), out Header headerAccounts))
        HeaderAccountsReplayed = headerAccounts;
      else
        Accounts.Drop();

    BlockchainBitcoin.LoadBlockchain();
    RollBackUnanchored();
    ResolveNodeWithoutState();

    if (HeaderAccountsReplayed != null)
      Task.Run(RunFullCheck);
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

  static void VerifyCoinbase(Block block, long rent)
  {
    long fee = 0;

    for (int i = 1; i < block.TXs.Count; i += 1)
    {
      if (block.TXs[i].Fee > SUPPLY_MAX - fee)
        throw new ProtocolException($"Fees of block exceed supply max.");

      fee += block.TXs[i].Fee;
    }

    if (GetBlockReward(block.Header.Height) + fee + rent != block.TXs[0].GetValueOutputs())
      throw new ProtocolException($"Output values of coinbase not equal to blockReward plus tx fees plus rent.");
  }

  TXBToken CreateTXCoinbase(int height, long fees, long rent)
  {
    TXOutputP2PKH outputCoinbase = new(GetBlockReward(height) + fees + rent, Wallet.GetHash160KeyReceiving(TXPool.CanReceive));

    TXBToken tXCoinbase = new()
    {
      HeightAccountCreated = height
    };

    tXCoinbase.TXOutputs.Add(outputCoinbase);

    tXCoinbase.SerializeUnsigned();

    return tXCoinbase;
  }

  protected override void HandleTipUpdated()
  {
    TryInstallSweeps();
    MineBlock();
  }

  void MineBlock()
  {
    if (!IsMining || !IsAccountsAtTip())
      return;

    bool isTipAwaited = ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(HashBlockAwaitedRoundMining);

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

    block.TXs.Insert(0, CreateTXCoinbase(height, block.TXs.Sum(t => t.Fee), Accounts.LoadSweep(height).rent));

    block.Header = new HeaderBToken()
    {
      Height = height,
      HashPrevious = ChainRoot.HeaderTipBlockchain.Hash,
      MerkleRoot = block.ComputeMerkleRoot()
    };

    try
    {
      ((HeaderBToken)block.Header).HashAccounts = Accounts.ComputeHash(block);
    }
    catch (ProtocolException exception)
    {
      Console.WriteLine($"Mining round at height {height} skipped: {exception.Message}");
      return;
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
      ResyncWhenBehind();

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

  void ResyncWhenBehind()
  {
    Chain chainTallest = ChainRoot.FindChainTallest();

    if (chainTallest.HeaderTip.Height - ChainRoot.HeaderTipBlockchain.Height > COUNT_MAX_BLOCKS_STORED + DEPTH_MARGIN_RESYNC)
    {
      Resync(chainTallest);
      UpdateTipBlockchain(ChainRoot);
    }
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
