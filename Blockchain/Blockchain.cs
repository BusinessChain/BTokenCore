using LiteDB;
using System.Security.Cryptography;
using System.Collections.Concurrent;


namespace BTokenCore;

internal partial class Blockchain
{
  const int MAX_COUNT_BLOCKS_STORED = 200;

  internal Token Token;

  Chain ChainRoot;

  LiteDatabase Database;
  ILiteCollection<BsonDocument> DatabaseHeaderCollection;
  ILiteCollection<BsonDocument> DatabaseBlockCollection;

  SemaphoreSlim SemaphoreBlockchain;

  internal Action<Block> OnBlockInserted;
  internal Action<Block> OnBlockRolledBack;

  ConcurrentBag<Block> PoolBlocks = new();


  internal Blockchain(
    Token token,
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
  {
    Token = token;
    SemaphoreBlockchain = semaphoreBlockchain;

    ChainRoot = new(Token.CreateHeaderGenesis(), isRoot: true);
    ChainRoot.HeaderRoot.HeaderParent = blockchainParent?.ChainRoot.HeaderRoot;

    Database = new ($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    if (blockchainParent == null)
      DatabaseHeaderCollection = Database.GetCollection<BsonDocument>("headers");

    DatabaseBlockCollection = Database.GetCollection<BsonDocument>("blocks");

    Token.LoadDatabase(Database);

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
      blockLoad.Parse();
    }
    catch (ProtocolException)
    {
      blockLoad.Header = null;
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
    SHA256 sHA256 = SHA256.Create();
    Block blockLoad = new(Token);

    int height = ChainRoot.HeaderRoot.Height + 1;
    BsonDocument bsonDocumentHeader = DatabaseHeaderCollection.FindById(height);

    while (bsonDocumentHeader != null)
      try
      {
        byte[] headerBytes = bsonDocumentHeader["headerBytes"].AsBinary;
        int startIndex = 0;

        Header header = Token.ParseHeader(headerBytes, ref startIndex, sHA256);

        if (!ChainRoot.TryAppendHeader(header))
          break;

        BsonDocument bsonDocumentBlock = DatabaseBlockCollection.FindById(height);
        if (bsonDocumentBlock != null)
        {
          blockLoad.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);
          blockLoad.Header = header;
          blockLoad.Parse();

          Token.InsertBlock(blockLoad);

          ChainRoot.HeaderTipBlockchain = header;

          AnnounceBlock(OnBlockInserted, blockLoad);
        }

        height++;
        bsonDocumentHeader = DatabaseHeaderCollection.FindById(height);
      }
      catch
      {
        break;
      }
  }

  internal void LoadHeadersFromBlocks()
  {
    Block blockLoad = new(Token);

    foreach (BsonDocument bsonDocumentBlock in DatabaseBlockCollection.Find(Query.All(Query.Ascending)))
      try
      {
        blockLoad.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);
        blockLoad.Header = null;
        blockLoad.Parse();

        Header header = blockLoad.Header;

        if (header.HashPrevious.IsAllBytesEqual(ChainRoot.HeaderTipBlockchain.Hash))
          ChainRoot.TryAppendHeader(header);
        else
        {
          header.Height = bsonDocumentBlock["_id"].AsInt32;
          ChainRoot = new(header, isRoot: true);
        }

        ChainRoot.HeaderTipBlockchain = header;
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

    ChainRoot.InsertHeadersInTree([headerPlaceholder], out Header headerLastInTree);

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
      block.Parse();
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

      ChainRoot.InsertHeadersInTree(headers, out Header headerLastInTree);

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
      ChainRoot.DropForksDeep(PoolBlocks);
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
      InsertBlock(block);
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

  void InsertBlock(Block block)
  {
    DatabaseHeaderCollection?.Upsert(new BsonDocument
    {
      ["_id"] = block.Header.Height,
      ["headerBytes"] = block.Header.Serialize()
    });

    DatabaseBlockCollection.DeleteMany(
      Query.LT("_id", ChainRoot.GetHeightBlockStoredOldest()));

    DatabaseBlockCollection.Upsert(new BsonDocument
    {
      ["_id"] = block.Header.Height,
      ["blockBytes"] = block.Buffer[..block.LengthDataPayload]
    });

    Database.Commit();

    AnnounceBlock(OnBlockInserted, block);

    PoolBlocks.Add(block);
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
      block.Parse();

      Database.BeginTrans();

      Token.RollBack(block);

      DatabaseHeaderCollection?.Delete(header.Height);
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