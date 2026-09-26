using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class Network
{
  SemaphoreSlim SemaphoreBlockchainRoot = new(1);
  internal Blockchain BlockchainRoot;

  string PathBlocksMined = "blocksMined";
  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();


  internal async Task StartHeaderSync(Peer peer)
  {
    try
    {
      await LockBlockchain();

      if (NetworkParent.BlockchainRoot.HeaderTip.Height > BlockchainRoot.HeaderTip.Height)
        GetHeadersMessage.SendGetHeaders(peer, GetLocator());
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task LockBlockchain()
  {
    if (NetworkParent != null)
      await NetworkParent.LockBlockchain();

    await SemaphoreBlockchainRoot.WaitAsync().ConfigureAwait(false);
  }

  internal void ReleaseLockBlockchain()
  {
    if (NetworkParent != null)
      NetworkParent.ReleaseLockBlockchain();
    else
      SemaphoreBlockchainRoot.Release();
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

    int height = 1;
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

      Blockchain chain = BlockchainRoot.InsertBlockInChain(block);

      if (chain == null)
      {
        block.Header = null;
        return block;
      }

      while (chain.TryGetBlockNext(out block, out bool isDirectionForward))
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

          NotifyChildNetworksOnAnchorTokens(
            block,
            (networkChild, tokenAnchor) => networkChild.InsertBlock(tokenAnchor));
        }
        else
        {
          Token.RollBack(block);

          DatabaseHeaderCollection.Delete(block.Header.Height);
          DatabaseBlockCollection.Delete(block.Header.Height);

          NotifyChildNetworksOnAnchorTokens(
            block,
            (networkChild, tokenAnchor) => networkChild.Rollback(tokenAnchor));
        }

        BlockchainRoot = chain;

        Token.ReturnBlock(block);
      }

      block = Token.GetBlock();
      block.Header = FetchHeaderDownload(headerTipPeer);

      return block;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }


  void NotifyChildNetworksOnAnchorTokens(
    Block block,
    Action<Network, TXOutputTokenAnchor> action)
  {
    Dictionary<byte[], TXOutputTokenAnchor> cacheAnchorTokens =
        new(new EqualityComparerByteArray());

    foreach (TX tX in block.TXs)
      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput is TXOutputTokenAnchor tokenAnchor &&
            cacheAnchorTokens.TryAdd(tokenAnchor.HashBlockReferenced, tokenAnchor))
          if (NetworksChild.Find(n => n.Token.IDToken.IsAllBytesEqual(tokenAnchor.IDToken)) is Network network)
            action(network, tokenAnchor);
  }
    
  void Rollback(TXOutputTokenAnchor tokenAnchor)
  {
    
  }

  void InsertBlock(TXOutputTokenAnchor tokenAnchor)
  {
    try
    {
      if (TryGetBlockMined(out Block block, tokenAnchor.HashBlockReferenced))
      {
        //BlockchainRoot.TryExtendHeaderchain(block.Header);

        // Hier ein sendBlock machen und intern zuerst header und dann wenn
        // getdata kommt blcok aus peer cache laden, statt wieder node anfragen.
        lock (LOCK_Peers)
          Peers.ForEach(p => HeadersMessage.SendHeaders(
            p,
            new List<byte[]> { block.Header.Hash }));

        //InsertBlock(ref block);
      }

      // Der User muss jeweils definieren, mit welcher fee Rate er die Verankerung bezahlen will.
      // Dem user kann im GUI auch ein Tool zur verfügung gestellt werden welches ihm 
      // erlaubt, die Fee Rate automatisiert zu steuern. z.B. anhand vergangener Fee Raten
      // oder Marktpreis Arbitrierung.

      if (IsMining)
      {
        block = Token.MineBlock(
          BlockchainRoot.HeaderTip.Height + 1,
          out TXOutputTokenAnchor anchorToken);

        block.Header.HashPrevious = BlockchainRoot.HeaderTip.Hash;

        block.Header.ComputeHash();

        block.Serialize();

        BlocksMinedCache.Add(block);

        block.WriteToDisk(PathBlocksMined); // write to LiteDB

        NetworkParent.MineTokenAnchor(anchorToken);
      }
    }
    catch
    {
      return;
    }
  }

  bool TryGetBlockMined(out Block block, byte[] hash)
  {
    block = BlocksMinedCache
      .Find(b => b.Header.Hash.IsAllBytesEqual(hash));

    if (block == null)
    {
      // get from LiteDB instead.
      string pathFileBlock = Path.Combine(PathBlocksMined, block.Header.Hash.ToHexString());

      if (!File.Exists(pathFileBlock))
        return false;

      block = new(Token, File.ReadAllBytes(pathFileBlock));
      block.Parse();
    }

    return true;
  }

  void MineTokenAnchor(TXOutputTokenAnchor tokenAnchor)
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

  List<byte[]> GetLocator()
  {
    lock (BlockchainRoot)
      return BlockchainRoot.GetLocator();
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