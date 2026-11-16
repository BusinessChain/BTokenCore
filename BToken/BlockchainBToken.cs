using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  byte[] IDToken;

  const int SIZE_BLOCK_MAX = 1 << 20;

  const long COUNT_BYTES_MAX_POOL = 100 * SIZE_BLOCK_MAX;

  const long BLOCK_REWARD_INITIAL = 20_000_000_000;
  const int PERIOD_HALVENING_BLOCK_REWARD = 52500;
  const long SUPPLY_MAX = 2 * BLOCK_REWARD_INITIAL * PERIOD_HALVENING_BLOCK_REWARD;

  const int COUNT_BITS_INDEX_SEGMENT = 11;
  const int COUNT_SEGMENTS = 1 << COUNT_BITS_INDEX_SEGMENT;
  const int COUNT_MAX_BLOCKS_STORED = COUNT_SEGMENTS + BITCOIN_REORG_MARGIN;

  Blockchain BlockchainParent;

  protected DatabaseAccounts Accounts = new();
  protected SynchronizerAccounts Synchronizer;

  PoolTXBToken TXPool;

  protected Miner MinerBlockchain;
  protected ILiteCollection<BsonDocument> BlocksStored;
  protected ILiteCollection<BsonDocument> BlocksStoredAwaitingAnchor;
  const string NAME_FIELD_HEIGHT = "height";
  const string NAME_FIELD_LOCK_TIME_ANCHOR = "lockTimeAnchor";


  internal BlockchainBToken(
    string nameToken,
    LiteDatabase liteDatabase,
    string iDToken,
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base(nameToken, liteDatabase, SIZE_BLOCK_MAX, new ParserBToken(iDToken), blockchainParent, semaphoreBlockchain)
  {
    IDToken = ((ParserBToken)Parser).IDToken;

    BlockchainParent = blockchainParent;

    BlocksStored = LiteDatabase.GetCollection<BsonDocument>("blocks");
    BlocksStoredAwaitingAnchor = LiteDatabase.GetCollection<BsonDocument>("blocksAwaitingAnchor");

    TXPool = new PoolTXBToken(Accounts, COUNT_BYTES_MAX_POOL);
    MinerBlockchain = new Miner(this);
    Synchronizer = new SynchronizerAccounts(this);

    OnBlockInserted += TXPool.RemoveTXs;
    OnBlockRolledBack += block =>
    {
      if (Synchronizer.IsAccountsAtTip())
        TXPool.ReturnTXs(block);
    };

    blockchainParent.OnBlockInserted += HandleBlockParentInserted;
    blockchainParent.OnBlockRolledBack += RemoveHeaderPlaceholder;

    Accounts.Load(LiteDatabase);
  }

  protected override Header CreateHeaderRoot()
  {
    HeaderBToken header = new()
    {
      HashPrevious = new byte[Crypto.LENGTH_HASH256],
      MerkleRoot = new byte[Crypto.LENGTH_HASH256],
      HashAccounts = DatabaseAccounts.ComputeHashSegmentEmpty(),
      Nonce = BitConverter.ToUInt32(((ParserBToken)Parser).IDToken)
    };

    header.ComputeHash();

    return header;
  }

  internal override bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor, out int lockTime)
  {
    int heightTip = ChainRoot.HeaderTipBlockchain.Height;
    lockTime = heightTip;

    byte[] hash160Funding = Wallet.FindHash160KeyFunding(h => TXPool.LoadAccountPending(h)?.Balance >= feeAnchor);

    if (hash160Funding == null)
      return false;

    Account account = TXPool.LoadAccountPending(hash160Funding);

    tokenAnchor.Serialize();

    TXBToken tXAnchor = new()
    {
      KeyPublic = Wallet.GetKeyPublic(hash160Funding),
      HeightAccountCreated = account.HeightAccountCreated,
      Nonce = account.Nonce,
      Fee = feeAnchor,
      LockTime = heightTip
    };

    tXAnchor.TXOutputs.Add(tokenAnchor);

    tXAnchor.Serialize(Wallet, IDToken);

    try
    {
      SubmitTX(tXAnchor.TXRaw);
    }
    catch (ProtocolException)
    {
      return false;
    }

    if (LoadAnchorsMissed().FirstOrDefault() is TXBToken anchorReplaced)
      TXsOwnedStored.Delete(anchorReplaced.Hash);

    return true;
  }

  internal override int CountAnchorsReplaceable(long feeAnchor)
  {
    return LoadAnchorsMissed().Count;
  }

  List<TXBToken> LoadAnchorsMissed()
  {
    return TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
      .Select(d => (TXBToken)ParseTXOwned(d))
      .Where(t => !t.IsLockTimeValid(ChainRoot.HeaderTipBlockchain.Height + 1))
      .ToList();
  }

  protected override void InsertBlock(Block block)
  {
    TXsOwnedStored.DeleteMany(Query.Between(NAME_FIELD_HEIGHT_CONFIRMED, 1, block.Header.Height - COUNT_MAX_BLOCKS_STORED));

    BlocksStoredAwaitingAnchor.Delete(block.Header.Hash);

    BlocksStored.Upsert(CreateDocumentBlockStored(block));

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

    BlocksStored.Delete(block.Header.Height);
  }

  protected override bool IsTXOwnedConfirmed(TX tX, Block block)
  {
    TXBToken tXOwned = (TXBToken)tX;

    return block.TXs.Skip(1).Cast<TXBToken>().Any(t =>
      t.IDAccountSource.IsAllBytesEqual(tXOwned.IDAccountSource)
      && t.HeightAccountCreated == tXOwned.HeightAccountCreated
      && t.Nonce == tXOwned.Nonce);
  }

  internal byte[] GetSegmentSweptByBlock(byte[] hashBlock)
  {
    return Synchronizer.GetSegmentSweptByBlock(hashBlock);
  }

  internal byte[] GetBlocksCompactSerialized(byte[] hash, int countBlocksCompactMax, int sizeBlocksCompactMax)
  {
    if (!ChainRoot.TryGetHeader(hash, out Header header)
      || ChainRoot.HeaderTipBlockchain.GetHeaderAncestor(header.Height) != header)
      return null;

    List<byte[]> blocksCompact = new();
    int countBytes = VarInt.GetBytes(countBlocksCompactMax).Length;

    for (int height = header.Height + 1; blocksCompact.Count < countBlocksCompactMax; height++)
    {
      if (BlocksCompact.FindById(height) is not BsonDocument documentBlock)
        break;

      byte[] bytesBlockCompact = documentBlock[NAME_FIELD_BLOCK_BYTES].AsBinary;

      if (countBytes + bytesBlockCompact.Length > sizeBlocksCompactMax)
        break;

      blocksCompact.Add(bytesBlockCompact);
      countBytes += bytesBlockCompact.Length;
    }

    if (blocksCompact.Count == 0)
      return null;

    return [.. VarInt.GetBytes(blocksCompact.Count), .. blocksCompact.SelectMany(c => c)];
  }

  internal Header FetchHeaderSweepMissingNext(Func<Header, bool> canSupplierDeliver)
  {
    return Synchronizer.FetchHeaderSweepMissingNext(canSupplierDeliver);
  }

  internal void AddSegmentDownload(byte[] hashBlock, byte[] buffer, int length)
  {
    Synchronizer.AddSegmentDownload(hashBlock, buffer, length);
  }

  void LoadTXsOwnedUnconfirmed()
  {
    if (!Synchronizer.IsAccountsAtTip())
      return;

    foreach (TXBToken tXOwned in TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).Select(ParseTXOwned).Cast<TXBToken>().OrderBy(t => t.Nonce).ToList())
      try
      {
        TXPool.AddTX(tXOwned);
      }
      catch (ProtocolException)
      { }
  }

  internal void AddTX(TX tX)
  {
    if (!Synchronizer.IsAccountsAtTip())
      throw new ProtocolException($"TX {tX.Hash.ToHexString()} cannot be verified without account database.");

    TXPool.AddTX(tX);
  }

  internal void SubmitTX(byte[] tXRaw)
  {
    int index = 0;

    TX tX = Parser.ParseTX(tXRaw, ref index, isCoinbase: false, verifySignatures: true);

    AddTX(tX);
    InsertTXOwned(tX);
  }

  internal override void Load()
  {
    LoadTree();

    Synchronizer.Load();

    BlockchainParent.Load();
    RollBackUnanchored();

    AnnounceBlocksLoaded();

    ResyncWhenBehindWindowStored();

    Synchronizer.Start();
  }

  void RollBackUnanchored()
  {
    Header header = ChainRoot.HeaderTip;

    while (header != ChainRoot.HeaderRoot && header.HeaderParent == null)
      header = header.HeaderPrevious;

    if (header.HeaderNext != null)
      RemoveHeader(ChainRoot, header.HeaderNext);
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

  // Coinbase fields besides outputs stay unchecked: a coinbase spends no account, so nothing can be replayed.
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

  protected void StoreBlockAwaitingAnchor(Block block, int? lockTimeAnchor)
  {
    BsonDocument documentBlock = new()
    {
      [NAME_FIELD_ID] = block.Header.Hash,
      [NAME_FIELD_HEIGHT] = block.Header.Height,
      [NAME_FIELD_BLOCK_BYTES] = block.Buffer[..block.LengthDataPayload]
    };

    if (lockTimeAnchor.HasValue)
      documentBlock[NAME_FIELD_LOCK_TIME_ANCHOR] = lockTimeAnchor.Value;

    BlocksStoredAwaitingAnchor.Upsert(documentBlock);
  }
   
  void InsertBlockAwaitingAnchor(Header headerParent, BsonDocument bsonDocumentBlock)
  {
    if (!ChainRoot.TryGetHeaderPlaceholder(headerParent, out Header headerPlaceholder, out Chain chain))
      return;

    if (!TryParseBlockStored(headerPlaceholder, bsonDocumentBlock, out Block block))
      return;

    ParserToken.DrawAnchorsWinner(block.Header, ParserToken.EnumerateAnchorsEligible(block.TXs, block.Header.Height), CountLevelsBelowRoot);

    if (chain.BlocksQueued.TryAdd(headerPlaceholder.Height, block))
      UpdateTipBlockchain(chain);
  }

  protected override ILiteCollection<BsonDocument> GetBlocksLoaded()
  {
    return BlocksStored;
  }

  internal override Block GetBlock(byte[] hash)
  {
    if (!ChainRoot.TryGetHeader(hash, out Header header) || BlocksStored.FindById(header.Height) is not BsonDocument documentBlock)
      return null;

    return new Block(documentBlock[NAME_FIELD_BLOCK_BYTES].AsBinary)
    {
      Header = header
    };
  }

  protected override void ParseBlockStored(Block block, BsonDocument documentBlock)
  {
    Parser.ParseBlock(block, verifySignatures: false);
  }

  protected BsonDocument CreateDocumentBlockStored(Block block)
  {
    return new BsonDocument
    {
      [NAME_FIELD_ID] = block.Header.Height,
      [NAME_FIELD_BLOCK_BYTES] = block.Buffer[..block.LengthDataPayload]
    };
  }

  void HandleBlockParentInserted(Block blockParent)
  {
    bool hasAnchorWinner = blockParent.Header.AnchorsWinner.TryGetValue(IDToken, out TXOutputTokenAnchor anchor);
    BsonDocument documentBlockReferenced = hasAnchorWinner ? BlocksStoredAwaitingAnchor.FindById(anchor.HashBlockReferenced) : null;

    if (hasAnchorWinner)
    {
      Header headerPlaceholder = new HeaderBToken(
        headerHash: anchor.HashBlockReferenced,
        hashPrevious: anchor.HashBlockPreviousReferenced,
        merkleRootHash: new byte[Crypto.LENGTH_HASH256],
        hashAccounts: new byte[Crypto.LENGTH_HASH256],
        nonce: 0)
      {
        HeaderParent = blockParent.Header
      };

      ChainRoot.InsertHeaderPlaceholder(headerPlaceholder, GetHeightBlockStoredOldest());

      if (documentBlockReferenced != null)
        InsertBlockAwaitingAnchor(blockParent.Header, documentBlockReferenced);
    }

    bool isBlockParentTip = blockParent.Header.HeaderNext == null;

    if (isBlockParentTip)
    {
      MinerBlockchain.ScheduleReplacementAnchorsMissed(hasAnchorWinner && documentBlockReferenced == null);

      ResyncWhenBehindWindowStored();

      BlocksStoredAwaitingAnchor.DeleteMany(
        Query.Between(NAME_FIELD_LOCK_TIME_ANCHOR, 0, blockParent.Header.Height - BITCOIN_REORG_MARGIN));
    }
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

    if (chainTallest.HeaderTip.Height - ChainRoot.HeaderTipBlockchain.Height <= COUNT_MAX_BLOCKS_STORED)
      return;

    Synchronizer.Drop();

    Reorg(chainTallest);

    BlocksStored.DeleteAll();

    Header headerRoot = ChainRoot.HeaderTip.GetHeaderAncestor(ChainRoot.HeaderTip.Height - COUNT_MAX_BLOCKS_STORED);

    ChainRoot.ReRoot(headerRoot);

    UpdateTipBlockchain(ChainRoot);
  }
}
