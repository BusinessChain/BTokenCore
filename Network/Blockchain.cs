using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class Blockchain
{
  internal Token Token;

  internal Network Network;

  internal LiteDatabase LiteDatabase;
  internal ILiteCollection<BsonDocument> DatabaseHeaderCollection;
  internal ILiteCollection<BsonDocument> DatabaseBlockCollection;


  internal Blockchain(
    ICommunication communication,
    Token token,
    Header headerRootParent,
    SemaphoreSlim semaphoreBlockchain,
    bool flagEnableInboundConnections,
    bool flagEnableRelay)
  {
    Token = token;
    SemaphoreBlockchain = semaphoreBlockchain;

    BlockchainRoot = new(Token.CreateHeaderGenesis());
    BlockchainRoot.HeaderRoot.HeaderParent = headerRootParent;

    Network = new(
      this,
      communication,
      token,
      flagEnableInboundConnections,
      flagEnableRelay);

    LiteDatabase = new LiteDatabase($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    DatabaseHeaderCollection = LiteDatabase.GetCollection<BsonDocument>("headers");
    DatabaseBlockCollection = LiteDatabase.GetCollection<BsonDocument>("blocks");
  }

  internal void Start()
  {
    LoadBlockchain();

    Network.Start();
  }


  SemaphoreSlim SemaphoreBlockchain;
  internal Branch BlockchainRoot;

  internal Action<Block> OnBlockInserted;


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

      header = BlockchainRoot.GetHeader(hash);

      bsonDocumentBlock = DatabaseBlockCollection.FindById(header.Height);
    }
    finally
    {
      ReleaseLockBlockchain();
    }

    if (bsonDocumentBlock != null)
    {
      blockLoad.Buffer = bsonDocumentBlock["blockBytes"].AsBinary;
      blockLoad.Header = header;
      blockLoad.Parse();
    }
  }

  void LoadBlockchain()
  {
    SHA256 sHA256 = SHA256.Create();
    Block blockLoad = new(Token);

    int height = BlockchainRoot.HeaderRoot.Height + 1;
    BsonDocument bsonDocumentHeader = DatabaseHeaderCollection.FindById(height);

    while (bsonDocumentHeader != null)
      try
      {
        byte[] headerBytes = bsonDocumentHeader["headerBytes"].AsBinary;
        int startIndex = 0;

        Header header = Token.ParseHeader(headerBytes, ref startIndex, sHA256);

        BlockchainRoot.AppendHeader(header);

        BsonDocument bsonDocumentBlock = DatabaseBlockCollection.FindById(height);
        if (bsonDocumentBlock != null)
        {
          blockLoad.Buffer = bsonDocumentHeader["blockBytes"].AsBinary;
          blockLoad.Header = header;
          blockLoad.Parse();

          Token.InsertBlock(blockLoad);

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
    if (headerTipPeer == null
      || headerTipPeer.Height <= BlockchainRoot.HeaderTipBlockchain.Height)
      return null;

    return BlockchainRoot.FindChain(headerTipPeer)?.FetchHeaderDownloadAlongPath(headerTipPeer.Height);
  }

  internal async Task<Header> TryExtendHeaderchain(List<Header> headers)
  {
    try
    {
      await LockBlockchain();

      return BlockchainRoot.TryExtendHeaderchain(headers);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<Block> InsertBlockReturnNextBlock(Block block, Header headerTipPeer)
  {
    try
    {
      await LockBlockchain();

      Branch branch = BlockchainRoot.QueueBlock(block);

      if (branch == null)
      {
        block.Header = null;
        return block;
      }

      //Aber sollte das nicht nur bei BlockchainRoot passieren?
      FlushBlocksToDatabase(branch);

      block = Token.GetBlock();
      block.Header = FetchHeaderDownload(headerTipPeer);

      return block;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  void FlushBlocksToDatabase(Branch chain)
  {
    while (chain.TryGetBlockNext(out Block block, out bool isDirectionForward))
    {
      if (isDirectionForward)
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
          ["blockBytes"] = block.Buffer
        });

        OnBlockInserted?.Invoke(block);
      }
      else
      {
        Token.RollBack(block);

        DatabaseHeaderCollection.Delete(block.Header.Height);
        DatabaseBlockCollection.Delete(block.Header.Height);
      }

      BlockchainRoot = chain; // is this necessary?

      Token.ReturnBlock(block);
    }
  }

  internal void InsertBlockMined(Block block)
  {
    Header header = block.Header;

    if (BlockchainRoot.QueueBlockMined(block) is Branch chain)
    {
      FlushBlocksToDatabase(chain);

      Network.AnnounceHeader(header);
    }
  }

  internal void Broadcast(TX tX)
  {
    Network.Broadcast(tX);
  }

  internal async Task<List<byte[]>> GetLocator()
  {
    try
    {
      await LockBlockchain();
      return BlockchainRoot.GetLocator();
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
      return BlockchainRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}
