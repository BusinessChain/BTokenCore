using LiteDB;
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
  const int DEPTH_MARGIN_REORG_PARENT = 6;

  DatabaseAccounts Accounts = new();
  SynchronizerAccounts Synchronizer;

  PoolTXBToken TXPool;

  byte[] IDToken = new byte[TXOutputTokenAnchor.LENGTH_ID_TOKEN];

  Blockchain BlockchainParent;

  Miner MinerBlockchain;
  ILiteCollection<BsonDocument> BlocksStoredAwaitingAnchor;
  const string NAME_FIELD_HEIGHT = "height";
  const string NAME_FIELD_LOCK_TIME_ANCHOR = "lockTimeAnchor";


  internal BlockchainBToken(
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base("TokenBToken", SIZE_BLOCK_MAX, blockchainParent, semaphoreBlockchain)
  {
    BlockchainParent = blockchainParent;

    BlocksStoredAwaitingAnchor = LiteDatabase.GetCollection<BsonDocument>("blocksAwaitingAnchor");

    TXPool = new PoolTXBToken(this);
    MinerBlockchain = new Miner(this);
    Synchronizer = new SynchronizerAccounts(this);

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

  internal override bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor, out int lockTime)
  {
    throw new NotImplementedException();
  }

  internal override int CountAnchorsMissed()
  {
    throw new NotImplementedException();
  }

  protected override void InsertBlock(Block block)
  {
    TXsOwnedStored.DeleteMany(Query.Between(NAME_FIELD_HEIGHT_CONFIRMED, 1, block.Header.Height - COUNT_MAX_BLOCKS_STORED));

    BlocksStoredAwaitingAnchor.Delete(block.Header.Hash);

    if (!Synchronizer.IsAccountsAtTip())
      return;

    int heightPrune = block.Header.Height - COUNT_MAX_BLOCKS_STORED + 1;

    BlocksStored.DeleteMany(Query.LT(NAME_FIELD_ID, heightPrune));
    BlocksStoredAwaitingAnchor.DeleteMany(Query.LT(NAME_FIELD_HEIGHT, heightPrune));

    Accounts.InsertBlock(block);
  }

  protected override void RollBackBlock(Block block)
  {
    StoreBlockAwaitingAnchor(block, null);

    Synchronizer.RollBack(block.Header);
  }

  protected override bool IsTXOwnedConfirmed(TX tX, Block block)
  {
    if (!Synchronizer.IsAccountsAtTip())
      return false;

    TXBToken tXBToken = (TXBToken)tX;

    Account account = Accounts.LoadAccount(tXBToken.IDAccountSource, heightAccountCreated: 0);

    return account.HeightAccountCreated != tXBToken.HeightAccountCreated || account.Nonce > tXBToken.Nonce;
  }

  internal byte[] GetSegmentSweptByBlock(byte[] hashBlock)
  {
    return Synchronizer.GetSegmentSweptByBlock(hashBlock);
  }

  internal Header FetchHeaderSweepMissingNext(Func<Header, bool> canSupplierDeliver)
  {
    return Synchronizer.FetchHeaderSweepMissingNext(canSupplierDeliver);
  }

  internal bool AddSegmentDownload(byte[] hashBlock, byte[] buffer, int length)
  {
    return Synchronizer.AddSegmentDownload(hashBlock, buffer, length);
  }

  void LoadTXsOwnedUnconfirmed()
  {
    if (!Synchronizer.IsAccountsAtTip())
      return;

    foreach (BsonDocument documentTXOwned in TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
      try
      {
        TXPool.AddTX(ParseTXOwned(documentTXOwned));
      }
      catch (ProtocolException)
      { }
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

    Synchronizer.Load();

    BlockchainParent.LoadBlockchain();
    RollBackUnanchored();

    ResyncWhenBehindWindowStored();

    Synchronizer.Start();
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

  internal void StartMining(int countAnchorsPer10Minutes, long feeAnchor)
  {
    MinerBlockchain.StartMining(countAnchorsPer10Minutes, feeAnchor);
  }

  internal void StopMining()
  {
    MinerBlockchain.StopMining();
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

  protected override void HandleTipUpdated()
  {
    Synchronizer.HandleTipUpdated();
  }

  void StoreBlockAwaitingAnchor(Block block, int? lockTimeAnchor)
  {
    BsonDocument documentBlock = new()
    {
      [NAME_FIELD_ID] = block.Header.Hash,
      [NAME_FIELD_HEIGHT] = block.Header.Height,
      [NAME_FIELD_BLOCK_BYTES] = SerializeBlockStored(block)
    };

    if (lockTimeAnchor.HasValue)
      documentBlock[NAME_FIELD_LOCK_TIME_ANCHOR] = lockTimeAnchor.Value;

    BlocksStoredAwaitingAnchor.Upsert(documentBlock);
  }

  void HandleBlockParentInserted(Block blockParent)
  {
    bool hasAnchorWinner = blockParent.Header.AnchorsWinner.TryGetValue(IDToken, out TXOutputTokenAnchor anchorWinner);
    bool isBlockParentTip = blockParent.Header.HeaderNext == null;

    if (isBlockParentTip)
      MinerBlockchain.HandleBlockParentTip(
        hasAnchorWinner && !BlocksStoredAwaitingAnchor.Exists(Query.EQ(NAME_FIELD_ID, anchorWinner.HashBlockReferenced)));

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
      ResyncWhenBehindWindowStored();

    if (hasAnchorWinner
      && ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder, out Chain chain)
      && BlocksStoredAwaitingAnchor.FindById(anchorWinner.HashBlockReferenced) is BsonDocument bsonDocumentBlock)
    {
      Block block = new(bsonDocumentBlock[NAME_FIELD_BLOCK_BYTES].AsBinary);

      if (TryParseBlockStored(headerPlaceholder, block) && chain.BlocksQueued.TryAdd(headerPlaceholder.Height, block))
        UpdateTipBlockchain(chain);
    }

    if (isBlockParentTip)
      BlocksStoredAwaitingAnchor.DeleteMany(
        Query.Between(NAME_FIELD_LOCK_TIME_ANCHOR, 0, blockParent.Header.Height - DEPTH_MARGIN_REORG_PARENT));
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

  void ResyncWhenBehindWindowStored()
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
    Synchronizer.Drop();
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
