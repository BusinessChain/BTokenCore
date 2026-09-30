using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class Network
{
  SemaphoreSlim SemaphoreBlockchain;
  internal Blockchain BlockchainRoot;

  internal Action<Block> OnBlockInserted;
  internal Action<TXOutputTokenAnchor> OnTokenAnchorMined;

  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();


  internal async Task StartHeaderSync(Peer peer)
  {
    try
    {
      await LockBlockchain();

      GetHeadersMessage.SendGetHeaders(peer, BlockchainRoot.GetLocator());
    }
    finally
    {
      ReleaseLockBlockchain();
    }
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

  const int TIMESPAN_LOOP_DISPATCHER_MILLISECONDS = 1000;
  const int TIMEOUT_BLOCK_REQUEST_SECONDS = 60;

  async Task StartBlockDownloadDispatcher()
  {
    while (true)
    {
      await Task.Delay(TIMESPAN_LOOP_DISPATCHER_MILLISECONDS).ConfigureAwait(false);

      List<Peer> peers;

      lock (LOCK_Peers)
        peers = Peers.ToList();

      foreach (Peer peer in peers)
      {
        if (peer.IsDisposed() || !peer.SemaphorePeer.Wait(0))
          continue;

        try
        {
          BlockMessage blockMessage = (BlockMessage)peer.ProtocolStateMachine[BlockMessage.Command];
          HeadersMessage headersMessage = (HeadersMessage)peer.ProtocolStateMachine[HeadersMessage.Command];

          if (blockMessage.BlockDownload.Header == null)
          {
            Header headerDownload;

            try
            {
              await LockBlockchain();
              headerDownload = FetchHeaderDownload(headersMessage.HeaderTipReceivedLast);
            }
            finally
            {
              ReleaseLockBlockchain();
            }

            if (headerDownload != null)
            {
              blockMessage.BlockDownload.Header = headerDownload;
              blockMessage.TimeRequestBlock = DateTime.UtcNow;
              await GetDataMessage.SendBlockRequest(peer, headerDownload.Hash);
            }
          }
          else if (DateTime.UtcNow - blockMessage.TimeRequestBlock > TimeSpan.FromSeconds(TIMEOUT_BLOCK_REQUEST_SECONDS))
            peer.SocketCommunication.Dispose();
        }
        catch
        {
          peer.SocketCommunication.Dispose();
        }
        finally
        {
          peer.SemaphorePeer.Release();
        }
      }
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

      Blockchain branch = BlockchainRoot.QueueBlock(block);

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

  void FlushBlocksToDatabase(Blockchain chain)
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

  internal void OnBlockParentInserted(TXOutputTokenAnchor anchorWinner)
  {
    try
    {
      if (anchorWinner != null)
        InsertBlockMined(anchorWinner);

      if (IsMining)
        MineBlockNext();
    }
    catch
    {
      return;
    }
  }

  void InsertBlockMined(TXOutputTokenAnchor anchorWinner)
  {
    if (!TryGetBlockMined(out Block block, anchorWinner.HashBlockReferenced))
      return;

    Header header = block.Header;

    if (BlockchainRoot.QueueBlockMined(block) is Blockchain chain)
    {
      FlushBlocksToDatabase(chain);

      lock (LOCK_Peers)
        Peers.ForEach(p => HeadersMessage.SendHeaders(
          p,
          new List<byte[]> { header.Serialize() }));
    }

    BlocksMinedCache.Remove(block);
    DatabaseBlocksMinedCollection.Delete(anchorWinner.HashBlockReferenced);
  }

  void MineBlockNext()
  {
    // The user has to define the fee rate at which they want to pay for the anchoring.
    // The GUI could also offer a tool that controls the fee rate automatically,
    // e.g. based on past fee rates or market price arbitrage.

    Block block = Token.MineBlock(
      BlockchainRoot.HeaderTipBlockchain,
      out TXOutputTokenAnchor anchorToken);

    block.Serialize();

    BlocksMinedCache.Add(block);

    DatabaseBlocksMinedCollection.Insert(new BsonDocument
    {
      ["_id"] = block.Header.Hash,
      ["blockBytes"] = block.Buffer
    });

    OnTokenAnchorMined(anchorToken);
  }

  bool TryGetBlockMined(out Block block, byte[] hash)
  {
    block = BlocksMinedCache
      .Find(b => b.Header.Hash.IsAllBytesEqual(hash));

    if (block == null)
    {
      BsonDocument bsonDocumentBlock = DatabaseBlocksMinedCollection.FindById(hash);

      if (bsonDocumentBlock == null)
        return false;

      block = new(Token, bsonDocumentBlock["blockBytes"].AsBinary);
      block.Parse();
    }

    return true;
  }

  internal void MineTokenAnchor(TXOutputTokenAnchor tokenAnchor)
  {
    if (Token.TryCreateTXAnchor(tokenAnchor, FeePerByte, out TX tX))
      lock (LOCK_Peers)
        foreach (Peer peer in Peers)
          peer.BroadcastTX(tX);
    else
    {
      IsMining = false;
    }
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