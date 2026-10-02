using LiteDB;
using System.Security.Cryptography;
using System.Collections.Concurrent;


namespace BTokenCore;

internal partial class Blockchain
{
  internal Token Token;

  Branch BranchRoot;

  internal Action<Block> OnBlockInserted;

  internal LiteDatabase LiteDatabase;
  internal ILiteCollection<BsonDocument> DatabaseHeaderCollection;
  internal ILiteCollection<BsonDocument> DatabaseBlockCollection;

  SemaphoreSlim SemaphoreBlockchain;

  ConcurrentBag<Block> PoolBlocks = new();


  internal Blockchain(
    Token token,
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
  {
    Token = token;
    SemaphoreBlockchain = semaphoreBlockchain;

    BranchRoot = new(Token.CreateHeaderGenesis(), isRoot: true);
    BranchRoot.HeaderRoot.HeaderParent = blockchainParent?.BranchRoot.HeaderRoot;

    LiteDatabase = new LiteDatabase($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    DatabaseHeaderCollection = LiteDatabase.GetCollection<BsonDocument>("headers");
    DatabaseBlockCollection = LiteDatabase.GetCollection<BsonDocument>("blocks");
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

      header = BranchRoot.GetHeader(hash);

      bsonDocumentBlock = DatabaseBlockCollection.FindById(header.Height);
    }
    finally
    {
      ReleaseLockBlockchain();
    }

    if (bsonDocumentBlock != null)
    {
      blockLoad.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);
      blockLoad.Header = header;
      blockLoad.Parse();
    }
  }

  internal int GetHeight()
  {
    return BranchRoot.HeaderTipBlockchain.Height;
  }

  internal Block MineBlock(out TXOutputTokenAnchor anchorToken)
  {
    return Token.MineBlock(BranchRoot.HeaderTipBlockchain, TakeBlockFromPool(), out anchorToken);
  }

  Block TakeBlockFromPool()
  {
    if (!PoolBlocks.TryTake(out Block block))
      block = new Block(Token);

    return block;
  }

  internal void LoadBlockchain()
  {
    SHA256 sHA256 = SHA256.Create();
    Block blockLoad = new(Token);

    int height = BranchRoot.HeaderRoot.Height + 1;
    BsonDocument bsonDocumentHeader = DatabaseHeaderCollection.FindById(height);

    while (bsonDocumentHeader != null)
      try
      {
        byte[] headerBytes = bsonDocumentHeader["headerBytes"].AsBinary;
        int startIndex = 0;

        Header header = Token.ParseHeader(headerBytes, ref startIndex, sHA256);

        BranchRoot.AppendHeader(header);

        BsonDocument bsonDocumentBlock = DatabaseBlockCollection.FindById(height);
        if (bsonDocumentBlock != null)
        {
          blockLoad.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);
          blockLoad.Header = header;
          blockLoad.Parse();

          Token.InsertBlock(blockLoad);

          BranchRoot.HeaderTipBlockchain = header;

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

  internal async Task<Header> TryExtendHeaderchain(List<Header> headers)
  {
    try
    {
      await LockBlockchain();

      return BranchRoot.TryExtendHeaderchain(headers);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<Header> GetHeaderDownload(Header headerTipPeer)
  {
    try
    {
      await LockBlockchain();

      return FetchHeaderDownload(headerTipPeer);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  Header FetchHeaderDownload(Header headerTipPeer)
  {
    if (headerTipPeer == null || headerTipPeer.Height <= BranchRoot.HeaderTipBlockchain.Height)
      return null;

    return BranchRoot.FindChain(headerTipPeer)?.FetchHeaderDownloadAlongPath(headerTipPeer.Height);
  }

  internal async Task<Block> InsertBlockReturnNextBlock(Block block, Header headerTipPeer)
  {
    try
    {
      await LockBlockchain();

      if (!BranchRoot.TryQueueBlock(block, out Branch branch))
      {
        block.Header = null;
        return block;
      }

      if (branch == BranchRoot)
        while (BranchRoot.TryGetBlockNext(out block))
          InsertBlock(block);
      else
      {
        while (branch.TryGetBlockNext(out block))
          branch.BlocksBranch.Add(block.Header.Height, block);

        if (branch.IsStrongerThan(BranchRoot))
          Reorg(branch);
      }

      block = TakeBlockFromPool();
      block.Header = FetchHeaderDownload(headerTipPeer);

      return block;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  void Reorg(Branch branch)
  {
    int heightFork = branch.HeaderRoot.Height - 1;

    while (BranchRoot.HeaderTipBlockchain.Height > heightFork)
    {
      Header header = BranchRoot.HeaderTipBlockchain;
      Block block = TakeBlockFromPool();

      block.LoadBuffer(DatabaseBlockCollection.FindById(header.Height)["blockBytes"].AsBinary);
      block.Header = header;
      block.Parse();

      Token.RollBack(block);

      DatabaseHeaderCollection.Delete(header.Height);
      DatabaseBlockCollection.Delete(header.Height);

      BranchRoot.HeaderTipBlockchain = header.HeaderPrevious;

      PoolBlocks.Add(block);
    }

    foreach (Block block in branch.TakeBlocksBranch())
      InsertBlock(block);

    branch.SwitchWithRootBranch(BranchRoot);
    BranchRoot = branch;
  }

  void InsertBlock(Block block)
  {
    Token.InsertBlock(block);

    DatabaseHeaderCollection.Insert(new BsonDocument
    {
      ["_id"] = block.Header.Height,
      ["headerBytes"] = block.Header.Serialize()
    });

    DatabaseBlockCollection.Insert(new BsonDocument
    {
      ["_id"] = block.Header.Height,
      ["blockBytes"] = block.Buffer[..block.LengthDataPayload]
    });

    OnBlockInserted?.Invoke(block);

    PoolBlocks.Add(block);
  }

  internal async Task<List<byte[]>> GetLocator()
  {
    try
    {
      await LockBlockchain();
      return BranchRoot.GetLocator();
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
      return BranchRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}