using LiteDB;
using System.Security.Cryptography;
using System.Collections.Concurrent;


namespace BTokenCore;

internal partial class Blockchain
{
  internal Token Token;

  Chain ChainRoot;

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

    LiteDatabase liteDatabase = new ($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    if (blockchainParent == null)
      DatabaseHeaderCollection = liteDatabase.GetCollection<BsonDocument>("headers");

    DatabaseBlockCollection = liteDatabase.GetCollection<BsonDocument>("blocks");
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

          OnBlockInserted?.Invoke(blockLoad);
        }

        height++;
        bsonDocumentHeader = DatabaseHeaderCollection.FindById(height);
      }
      catch
      {
        break;
      }
  }

  internal void InsertHeaderPlaceholder(Block blockParent)
  {
    if (!blockParent.Header.AnchorsWinner.TryGetValue(Token.IDToken, out TXOutputTokenAnchor anchorWinner))
      return;

    Header headerPlaceholder = Token.CreateHeaderPlaceholder(anchorWinner, blockParent.Header);

    ChainRoot.InsertHeadersInTree([headerPlaceholder], out Header headerLastInTree);

    bool flagPlaceholderNotInsertedInTree = headerLastInTree != headerPlaceholder;

    if (flagPlaceholderNotInsertedInTree || !TryLoadBlock(headerPlaceholder.Height, out Block block))
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

    InsertBlocksQueued(chain);
  }

  internal void RemoveHeaderPlaceholder(Block blockParent)
  {
    if (!ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder))
      return;

    if (ChainRoot.HeaderTipBlockchain == headerPlaceholder)
      RollBack(heightAfterRollBack: headerPlaceholder.Height - 1);

    Chain chain = ChainRoot.FindChain(headerPlaceholder);

    chain.RemoveHeaderTip();

    if (chain.Blocks.Remove(headerPlaceholder.Height, out Block blockQueued))
      PoolBlocks.Add(blockQueued);
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

      InsertBlocksQueued(chain);

      return true;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  void InsertBlocksQueued(Chain chain)
  {
    if (chain != ChainRoot)
    {
      chain.AdvanceTipChain();

      if (chain.IsStrongerThan(ChainRoot))
        Reorg(chain);
    }

    while (ChainRoot.Blocks.Remove(ChainRoot.HeaderTipBlockchain.Height + 1, out Block block))
    {
      InsertBlock(block);
      ChainRoot.HeaderTipBlockchain = block.Header;
    }
  }

  void InsertBlock(Block block)
  {
    Token.InsertBlock(block);

    DatabaseHeaderCollection?.Upsert(new BsonDocument
    {
      ["_id"] = block.Header.Height,
      ["headerBytes"] = block.Header.Serialize()
    });

    DatabaseBlockCollection.Upsert(new BsonDocument
    {
      ["_id"] = block.Header.Height,
      ["blockBytes"] = block.Buffer[..block.LengthDataPayload]
    });

    OnBlockInserted?.Invoke(block);

    PoolBlocks.Add(block);
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

      Token.RollBack(block);

      DatabaseHeaderCollection?.Delete(header.Height);
      DatabaseBlockCollection.Delete(header.Height);

      OnBlockRolledBack?.Invoke(block);

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