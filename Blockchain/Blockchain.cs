using LiteDB;
using System.Collections.Concurrent;


namespace BTokenCore;

internal abstract partial class Blockchain
{
  internal const int MAX_COUNT_BLOCKS_STORED = 200;

  internal Wallet Wallet;

  internal int SizeBlockMax;

  protected Chain ChainRoot;

  protected LiteDatabase Database;
  protected ILiteCollection<BsonDocument> DatabaseBlockCollection;

  SemaphoreSlim SemaphoreBlockchain;

  internal Action<Block> OnBlockInserted;
  internal Action<Block> OnBlockRolledBack;
  internal Action<Header> OnTipChanged;

  Header HeaderTipBlockchainAnnounced;

  protected ConcurrentBag<Block> PoolBlocks = new();


  protected Blockchain(
    string nameToken,
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
  {
    Directory.CreateDirectory(nameToken);

    Wallet = new Wallet(File.ReadAllText($"Wallet{nameToken}/wallet"));

    SemaphoreBlockchain = semaphoreBlockchain;

    ChainRoot = new(CreateHeaderGenesis(), isRoot: true);
    ChainRoot.HeaderRoot.HeaderParent = blockchainParent?.ChainRoot.HeaderRoot;

    Database = new ($"Filename={nameToken + "Network"}.db;Mode=Exclusive");
    DatabaseBlockCollection = Database.GetCollection<BsonDocument>("blocks");

    AppDomain.CurrentDomain.ProcessExit += (s, e) => { Database.Dispose(); };
  }

  internal async Task<T> RunLocked<T>(Func<T> function)
  {
    await SemaphoreBlockchain.WaitAsync().ConfigureAwait(false);

    try
    {
      return function();
    }
    finally
    {
      SemaphoreBlockchain.Release();
    }
  }

  internal async Task RunLocked(Action action)
  {
    await RunLocked(() =>
    {
      action();
      return true;
    });
  }

  protected abstract Header CreateHeaderGenesis();

  internal abstract Header ParseHeader(byte[] buffer, ref int index);

  internal abstract TX ParseTX(byte[] buffer, ref int index, bool flagIsCoinbase = false);

  internal abstract Task<TX> GetTX(byte[] hash);

  protected void ParseBlockHeader(Block block, ref int startIndex)
  {
    Header header = ParseHeader(block.Buffer, ref startIndex);

    if (block.Header == null)
      block.Header = header;
    else if (block.Header.Hash.IsAllBytesEqual(header.Hash) && block.Header.HashPrevious.IsAllBytesEqual(header.HashPrevious))
      header.CopyTo(block.Header);
    else
      throw new ProtocolException($"Received unexpected block {header} expected was {block.Header}.");
  }

  void ParseBlock(Block block)
  {
    block.TXs.Clear();
    int startIndex = 0;

    ParseBlockHeader(block, ref startIndex);

    block.Header.CountTXs = VarInt.GetInt(block.Buffer, ref startIndex);

    if (block.Header.CountTXs == 0)
      throw new ProtocolException($"Block {block} lacks coinbase transaction.");

    HashSet<byte[]> hashesTX = new(new EqualityComparerByteArray());

    for (int t = 0; t < block.Header.CountTXs; t += 1)
    {
      TX tX = ParseTX(block.Buffer, ref startIndex, flagIsCoinbase: t == 0);

      if (!hashesTX.Add(tX.Hash))
        throw new ProtocolException($"Block {block.Header.Hash.ToHexString()} contains transaction {tX.Hash.ToHexString()} twice.");

      block.TXs.Add(tX);
    }

    if (!block.Header.MerkleRoot.IsAllBytesEqual(block.ComputeMerkleRoot()))
      throw new ProtocolException("Header merkle root not equal to computed transactions merkle root.");

    block.Header.Fee = block.TXs.Sum(t => t.Fee);

    block.Header.VerifyCoinbase(block.TXs[0].GetValueOutputs());

    foreach (TX tX in block.TXs)
      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput is TXOutputTokenAnchor tokenAnchor)
          block.Header.AnchorsWinner.TryAdd(tokenAnchor.IDToken, tokenAnchor);
  }

  protected virtual void ParseBlockStored(Block block)
  {
    ParseBlock(block);
  }

  protected abstract byte[] SerializeBlockStored(Block block);

  internal Block TakeBlockFromPool()
  {
    if (!PoolBlocks.TryTake(out Block block))
      block = new Block(SizeBlockMax);

    return block;
  }

  protected bool TryLoadBlock(int height, Block block)
  {
    BsonDocument bsonDocumentBlock = DatabaseBlockCollection.FindById(height);

    if (bsonDocumentBlock == null)
      return false;

    block.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);

    return true;
  }

  protected bool TryParseBlockStored(Header header, Block block)
  {
    block.Header = header;

    try
    {
      ParseBlockStored(block);
    }
    catch (ProtocolException)
    {
      block.Header = null;
      return false;
    }

    return true;
  }

  internal async Task GetBlock(byte[] hash, Block blockLoad)
  {
    Header header = null;

    if (await RunLocked(() => ChainRoot.TryGetHeader(hash, out header))
      && TryLoadBlock(header.Height, blockLoad))
      TryParseBlockStored(header, blockLoad);
  }

  internal void LoadBlockchain()
  {
    Block blockLoad = new(SizeBlockMax);

    foreach (BsonDocument bsonDocumentBlock in DatabaseBlockCollection.Find(Query.All(Query.Ascending)))
      try
      {
        blockLoad.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);
        blockLoad.Header = null;
        ParseBlockStored(blockLoad);

        Header header = blockLoad.Header;

        if (header.HashPrevious.IsAllBytesEqual(ChainRoot.HeaderTipBlockchain.Hash))
          ChainRoot.TryAppendHeader(header);
        else
        {
          header.Height = bsonDocumentBlock["_id"].AsInt32;
          ChainRoot = new(header, isRoot: true);
        }

        ChainRoot.HeaderTipBlockchain = header;

        OnBlockInserted.Announce(blockLoad);
      }
      catch
      {
        continue;
      }
  }

  protected int GetHeightBlockStoredOldest()
  {
    BsonDocument bsonDocumentBlockOldest = DatabaseBlockCollection.Query().OrderBy("_id").Select("_id").FirstOrDefault();

    if (bsonDocumentBlockOldest == null)
      return ChainRoot.HeaderRoot.Height;

    return bsonDocumentBlockOldest["_id"].AsInt32;
  }

  protected abstract void InsertBlock(Block block);

  protected virtual void RollBackBlock(Block block) { }

  internal Task<bool> TryInsertBlock(Block block)
  {
    return RunLocked(() =>
    {
      if (!ChainRoot.TryRemoveHeaderAwaited(block.Header.Hash, out Header headerAwaited, out Chain chainAwaiting))
        return false;

      block.Header = headerAwaited;
      ParseBlock(block);
      chainAwaiting.Blocks.Add(headerAwaited.Height, block);

      UpdateTipBlockchain(chainAwaiting);

      return true;
    });
  }

  protected void UpdateTipBlockchain(Chain chain)
  {
    if (chain != ChainRoot)
      chain.AdvanceTipChain();

    do
    {
      InsertBlocksQueued();
      ChainRoot.DropForksDeep(GetHeightBlockStoredOldest(), PoolBlocks);
    }
    while (TryReorgToForkStrongest());

    if (ChainRoot.HeaderTipBlockchain != HeaderTipBlockchainAnnounced)
    {
      HeaderTipBlockchainAnnounced = ChainRoot.HeaderTipBlockchain;
      OnTipChanged.Announce(HeaderTipBlockchainAnnounced);
    }
  }

  void InsertBlocksQueued()
  {
    while (ChainRoot.Blocks.Remove(ChainRoot.HeaderTipBlockchain.Height + 1, out Block block))
    {
      Database.BeginTrans();

      try
      {
        InsertBlock(block);

        ChainRoot.HeaderTipBlockchain = block.Header;

        DatabaseBlockCollection.Upsert(new BsonDocument
        {
          ["_id"] = block.Header.Height,
          ["blockBytes"] = SerializeBlockStored(block)
        });
      }
      catch (Exception exception)
      {
        Database.Rollback();

        if (exception is not ProtocolException)
          throw;

        PoolBlocks.Add(block);
        ChainRoot.CutOff(block.Header, PoolBlocks);
        continue;
      }

      Database.Commit();

      OnBlockInserted.Announce(block);

      PoolBlocks.Add(block);
    }
  }

  bool TryReorgToForkStrongest()
  {
    if (ChainRoot.FindForkStrongest() is not Chain chainForkStrongest
      || !chainForkStrongest.IsStrongerThan(ChainRoot))
      return false;

    Reorg(chainForkStrongest);
    return true;
  }

  void Reorg(Chain chain)
  {
    while (chain != ChainRoot)
    {
      if(chain.ChainParent == ChainRoot)
        RollBack(heightAfterRollBack: chain.HeaderRoot.Height - 1);

      chain.SwitchWithParent();
      chain = chain.ChainParent;
    }
  }

  protected void RollBack(int heightAfterRollBack)
  {
    while (ChainRoot.HeaderTipBlockchain.Height > heightAfterRollBack)
    {
      Header header = ChainRoot.HeaderTipBlockchain;
      Block block = TakeBlockFromPool();

      if (!TryLoadBlock(header.Height, block) || !TryParseBlockStored(header, block))
        throw new InvalidOperationException($"Stored block at height {header.Height} cannot be loaded for rollback.");

      Database.BeginTrans();

      try
      {
        RollBackBlock(block);

        DatabaseBlockCollection.Delete(header.Height);
      }
      catch
      {
        Database.Rollback();
        throw;
      }

      Database.Commit();

      OnBlockRolledBack.Announce(block);

      ChainRoot.HeaderTipBlockchain = header.HeaderPrevious;

      ChainRoot.Blocks.Add(header.Height, block);
    }
  }

  internal virtual Task<byte[]> GetDB(byte[] hash)
  {
    return Task.FromResult<byte[]>(null);
  }

  internal virtual Task<byte[]> GetHashDBMissing()
  {
    return Task.FromResult<byte[]>(null);
  }

  internal virtual Task InstallDB(byte[] hash, byte[] buffer, int length)
  {
    return Task.CompletedTask;
  }

  internal int GetHeight()
  {
    return ChainRoot.HeaderTipBlockchain.Height;
  }

  internal Task<Header> TryInsertHeadersInTree(List<Header> headers)
  {
    return RunLocked(() =>
    {
      ChainRoot.InsertHeadersInTree(headers, GetHeightBlockStoredOldest(), out Header headerLastInTree);

      return headerLastInTree;
    });
  }

  internal Task<Header> FetchHeaderBlockMissingNext(Func<Header, bool> canSupplierDeliverBlock)
  {
    return RunLocked(() => ChainRoot.FetchHeaderBlockMissingNextInTree(
      ChainRoot.HeaderTipBlockchain.Height,
      canSupplierDeliverBlock));
  }

  internal Task<List<byte[]>> GetLocator()
  {
    return RunLocked(() => ChainRoot.GetLocator());
  }

  internal Task<(List<byte[]> headers, int heightAncestor)> GetHeadersSerialized(
    List<byte[]> hashesLocator,
    int maxCountHeaders)
  {
    return RunLocked(() => ChainRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders));
  }
}
