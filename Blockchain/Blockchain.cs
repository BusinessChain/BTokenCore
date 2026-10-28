using LiteDB;
using System.Collections.Concurrent;


namespace BTokenCore;

internal abstract partial class Blockchain
{
  const int MAX_COUNT_BLOCKS_STORED = 200;

  internal Token Token;

  protected Chain ChainRoot;

  LiteDatabase Database;
  protected ILiteCollection<BsonDocument> DatabaseBlockCollection;
  IDatabaseToken DatabaseToken;

  SemaphoreSlim SemaphoreBlockchain;

  internal Action<Block> OnBlockInserted;
  internal Action<Block> OnBlockRolledBack;

  ConcurrentBag<Block> PoolBlocks = new();


  protected Blockchain(
    Token token,
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
  {
    Token = token;
    SemaphoreBlockchain = semaphoreBlockchain;

    ChainRoot = new(Token.CreateHeaderGenesis(), isRoot: true);
    ChainRoot.HeaderRoot.HeaderParent = blockchainParent?.ChainRoot.HeaderRoot;

    Database = new ($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    DatabaseBlockCollection = Database.GetCollection<BsonDocument>("blocks");

    DatabaseToken = Token.GetDatabase();

    if (DatabaseToken != null && !DatabaseToken.TryLoad(Database))
      DatabaseBlockCollection.DeleteAll();

    AppDomain.CurrentDomain.ProcessExit += (s, e) => { Database.Dispose(); };
  }

  internal async Task LockBlockchain()
  {
    await SemaphoreBlockchain.WaitAsync().ConfigureAwait(false);
  }

  internal void ReleaseLockBlockchain()
  {
    SemaphoreBlockchain.Release();
  }

  internal async Task GetBlock(byte[] hash, Block blockLoad)
  {
    Header header;
    BsonDocument bsonDocumentBlock;

    try
    {
      await LockBlockchain();

      if (!ChainRoot.TryGetHeader(hash, out header))
        return;

      bsonDocumentBlock = DatabaseBlockCollection.FindById(header.Height);
    }
    finally
    {
      ReleaseLockBlockchain();
    }

    if (bsonDocumentBlock == null)
      return;

    blockLoad.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);
    blockLoad.Header = header;

    try
    {
      ParseBlockStored(blockLoad);
    }
    catch (ProtocolException)
    {
      blockLoad.Header = null;
    }
  }

  internal async Task<byte[]> GetDB(byte[] hash)
  {
    try
    {
      await LockBlockchain();

      if (ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(hash) && DatabaseToken != null && DatabaseToken.Exists())
        return DatabaseToken.Serialize();

      return null;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<byte[]> GetHashDBMissing()
  {
    try
    {
      await LockBlockchain();

      if (DatabaseToken != null && !DatabaseToken.Exists())
        return ChainRoot.HeaderTipBlockchain.Hash;

      return null;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task InstallDB(byte[] hash, byte[] buffer, int length)
  {
    try
    {
      await LockBlockchain();

      if (DatabaseToken.Exists()
        || !ChainRoot.TryGetHeader(hash, out Header header)
        || header.Height > ChainRoot.HeaderTipBlockchain.Height)
        return;

      RollBack(heightAfterRollBack: header.Height);

      Database.BeginTrans();

      try
      {
        DatabaseToken.Install(buffer, length);
      }
      catch
      {
        Database.Rollback();
        DatabaseToken.Drop();
        throw;
      }

      Database.Commit();

      UpdateTipBlockchain(ChainRoot);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal int GetHeight()
  {
    return ChainRoot.HeaderTipBlockchain.Height;
  }

  internal Block MineBlock(out TXOutputTokenAnchor anchorToken)
  {
    return Token.MineBlock(ChainRoot.HeaderTipBlockchain, TakeBlockFromPool(), out anchorToken);
  }

  internal Block TakeBlockFromPool()
  {
    if (!PoolBlocks.TryTake(out Block block))
      block = new Block(Token);

    return block;
  }

  internal void LoadBlockchain()
  {
    Block blockLoad = new(Token);

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

        AnnounceBlock(OnBlockInserted, blockLoad);
      }
      catch
      {
        continue;
      }
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

  internal void InsertHeaderPlaceholder(Block blockParent)
  {
    if (!blockParent.Header.AnchorsWinner.TryGetValue(Token.IDToken, out TXOutputTokenAnchor anchorWinner))
      return;

    Header headerPlaceholder = Token.CreateHeaderPlaceholder(anchorWinner, blockParent.Header);

    Header headerLastInTree = ChainRoot.HeaderRoot;

    if (!headerLastInTree.Hash.IsAllBytesEqual(headerPlaceholder.Hash))
      ChainRoot.InsertHeadersInTree([headerPlaceholder], GetHeightBlockStoredOldest(), out headerLastInTree);

    if (headerLastInTree == null)
      return;

    if (headerLastInTree != headerPlaceholder)
    {
      headerLastInTree.HeaderParent = blockParent.Header;
      return;
    }

    if (!TryLoadBlock(headerPlaceholder.Height, out Block block))
      return;

    try
    {
      block.Header = headerPlaceholder;
      ParseBlockStored(block);
    }
    catch (ProtocolException)
    {
      PoolBlocks.Add(block);
      return;
    }

    Chain chain = ChainRoot.FindChain(headerPlaceholder);
    chain.Blocks.Add(headerPlaceholder.Height, block);

    UpdateTipBlockchain(chain);
  }

  internal void TriggerBootstrap(Block blockParent)
  {
    bool isBlockParentTip = blockParent.Header.HeaderNext == null;

    if (isBlockParentTip && ChainRoot.HeaderTip.Height - ChainRoot.HeaderTipBlockchain.Height > MAX_COUNT_BLOCKS_STORED)
    {
      DatabaseToken.Drop();
      DatabaseBlockCollection.DeleteAll();

      Header headerRoot = ChainRoot.HeaderTip;

      while (headerRoot.Height > ChainRoot.HeaderTip.Height - MAX_COUNT_BLOCKS_STORED)
        headerRoot = headerRoot.HeaderPrevious;

      ChainRoot.ReRoot(headerRoot, PoolBlocks);

      UpdateTipBlockchain(ChainRoot);
    }
  }

  internal void RemoveHeaderPlaceholder(Block blockParent)
  {
    if (!ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder))
      return;

    RemoveHeader(ChainRoot.FindChain(headerPlaceholder), headerPlaceholder);
  }

  void RemoveHeader(Chain chain, Header header)
  {
    if (chain == ChainRoot)
      RollBack(heightAfterRollBack: header.Height - 1);

    chain.CutOff(header, PoolBlocks);

    UpdateTipBlockchain(ChainRoot);
  }

  int GetHeightBlockStoredOldest()
  {
    BsonDocument bsonDocumentBlockOldest = DatabaseBlockCollection.Query().OrderBy("_id").Select("_id").FirstOrDefault();

    if (bsonDocumentBlockOldest == null)
      return ChainRoot.HeaderRoot.Height;

    return bsonDocumentBlockOldest["_id"].AsInt32;
  }

  protected abstract void ParseBlockStored(Block block);

  protected abstract byte[] SerializeBlockStored(Block block);

  bool TryLoadBlock(int height, out Block block)
  {
    block = null;

    BsonDocument bsonDocumentBlock = DatabaseBlockCollection.FindById(height);
    if (bsonDocumentBlock == null)
      return false;

    block = TakeBlockFromPool();
    block.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);

    return true;
  }

  internal async Task<Header> TryInsertHeadersInTree(List<Header> headers)
  {
    try
    {
      await LockBlockchain();

      ChainRoot.InsertHeadersInTree(headers, GetHeightBlockStoredOldest(), out Header headerLastInTree);

      return headerLastInTree;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<Header> FetchHeaderBlockMissingNext(
    Header headerTipSupplier,
    Func<Header, bool> canSupplierDeliverBlock)
  {
    try
    {
      await LockBlockchain();

      if (headerTipSupplier == null)
        return ChainRoot.FetchHeaderBlockMissingNextInTree(
          ChainRoot.HeaderTipBlockchain.Height,
          canSupplierDeliverBlock);

      bool isTipSupplierAhead = headerTipSupplier.Height > ChainRoot.HeaderTipBlockchain.Height;

      if (!isTipSupplierAhead)
        return null;

      return ChainRoot.FindChain(headerTipSupplier)?.FetchHeaderBlockMissingNextInChain(
        headerTipSupplier.Height,
        canSupplierDeliverBlock);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<bool> TryInsertBlock(Block block)
  {
    try
    {
      await LockBlockchain();

      if (!ChainRoot.TryQueueBlock(block, out Chain chain))
        return false;

      UpdateTipBlockchain(chain);

      return true;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  void UpdateTipBlockchain(Chain chain)
  {
    if (chain != ChainRoot)
      chain.AdvanceTipChain();

    do
    {
      InsertBlocksQueued();
      ChainRoot.DropForksDeep(GetHeightBlockStoredOldest(), PoolBlocks);
    }
    while (TryReorgToForkStrongest());
  }

  void InsertBlocksQueued()
  {
    while (ChainRoot.Blocks.Remove(ChainRoot.HeaderTipBlockchain.Height + 1, out Block block))
    {
      Database.BeginTrans();

      try
      {
        Token.InsertBlock(block);
      }
      catch (ProtocolException)
      {
        Database.Rollback();
        PoolBlocks.Add(block);
        ChainRoot.CutOff(block.Header, PoolBlocks);
        continue;
      }

      ChainRoot.HeaderTipBlockchain = block.Header;

      DatabaseBlockCollection.Upsert(new BsonDocument
      {
        ["_id"] = block.Header.Height,
        ["blockBytes"] = SerializeBlockStored(block)
      });

      Database.Commit();

      AnnounceBlock(OnBlockInserted, block);

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

  void AnnounceBlock(Action<Block> listeners, Block block)
  {
    if (listeners == null)
      return;

    foreach (Action<Block> listener in listeners.GetInvocationList())
      try
      {
        listener(block);
      }
      catch (Exception exception)
      {
        Console.WriteLine(
          $"Listener {listener.Method.DeclaringType.Name}.{listener.Method.Name} " +
          $"failed on block {block.Header}: {exception}");
      }
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

  void RollBack(int heightAfterRollBack)
  {
    while (ChainRoot.HeaderTipBlockchain.Height > heightAfterRollBack)
    {
      Header header = ChainRoot.HeaderTipBlockchain;
      TryLoadBlock(header.Height, out Block block);
      block.Header = header;
      ParseBlockStored(block);

      Database.BeginTrans();

      Token.RollBack(block);

      DatabaseBlockCollection.Delete(header.Height);

      Database.Commit();

      AnnounceBlock(OnBlockRolledBack, block);

      ChainRoot.HeaderTipBlockchain = header.HeaderPrevious;

      ChainRoot.Blocks.Add(header.Height, block); 
    }
  }

  internal async Task<List<byte[]>> GetLocator()
  {
    try
    {
      await LockBlockchain();
      return ChainRoot.GetLocator();
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<(List<byte[]> headers, int heightAncestor)> GetHeadersSerialized(
    List<byte[]> hashesLocator,
    int maxCountHeaders)
  {
    try
    {
      await LockBlockchain();
      return ChainRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}