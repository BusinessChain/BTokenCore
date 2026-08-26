using LiteDB;
using System.Collections.Concurrent;
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

  internal ConcurrentBag<Block> PoolBlocks = new();


  internal async Task<bool> TryLockBlockchain(int timeoutMilliSeconds)
  {
    if (NetworkParent != null)
      return await NetworkParent.TryLockBlockchain(timeoutMilliSeconds);

    return await SemaphoreBlockchainRoot.WaitAsync(timeoutMilliSeconds).ConfigureAwait(false);
  }

  void ReleaseLockBlockchain()
  {
    if (NetworkParent != null)
      NetworkParent.ReleaseLockBlockchain();
    else
      SemaphoreBlockchainRoot.Release();
  }

  void LoadBlockchain()
  {
    SHA256 sHA256 = SHA256.Create();
    Block blockLoad = new(Token);

    int height = 1;
    BsonDocument headerDB = DatabaseHeaderCollection.FindById(height);

    while (headerDB != null)
      try
      {
        byte[] headerBytes = headerDB["headerBytes"].AsBinary;
        int startIndex = 0;

        Header header = Token.ParseHeader(headerBytes, ref startIndex, sHA256);

        BlockchainRoot.AppendHeader(header);

        BsonDocument blockDB = DatabaseBlockCollection.FindById(height);
        if (blockDB != null)
        {
          blockLoad.Buffer = headerDB["blockBytes"].AsBinary;
          blockLoad.Header = header;
          blockLoad.Parse();

          Token.InsertBlock(blockLoad);
        }

        height++;
        headerDB = DatabaseHeaderCollection.FindById(height);
      }
      catch
      {
        break;
      }
  }

  internal async Task<List<byte[]>> ExtendHeaderchain(
    Header headerRoot,
    Block blockDownload)
  {
    List<byte[]> headerslocator = null;

    if (!await TryLockBlockchain(10000))
      return headerslocator;

    try
    {
      BlockchainRoot.TryExtendHeaderchain(
        headerRoot,
        out headerslocator,
        blockDownload);

      return headerslocator;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  /// <summary>
  /// Returns block because ref block is not possible with async.
  /// </summary>
  internal async Task<Block> InsertBlockReturnNextBlock(Block block)
  {
    Block blockNext = null;

    if (!await TryLockBlockchain(timeoutMilliSeconds: 10000))
      return block;

    try
    {
      do
      {
        if (!BlockchainRoot.TryInsertBlock(block, out blockNext, out bool flagBlockInsertedInRoot))
          return block;

        if (flagBlockInsertedInRoot)
        {
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

          NotifyChildNetworksOfAnchorToken(block);
        }

        PoolBlocks.Add(block);

      } while (blockNext != null);
    }
    finally
    {
      ReleaseLockBlockchain();
    }


    try
    {
      Blockchain chain = BlockchainRoot.FindChain(block);

      if (chain == null)
        return block;

      do
      {
        chain.InsertBlock(block); // falls ein weiterer existiert
        // der inserted werden kann, könnte der hier einfach returned werden

        if (chain == BlockchainRoot)
          NotifyChildNetworksOfAnchorToken(block);

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

        PoolBlocks.Add(block);

      } while (chain.QueueBlocks.TryGetValue(chain.HeaderTipBlockchain.Height + 1, out block));

      if (chain.IsStrongerThan(BlockchainRoot))
        ReorgBlockchain(chain);

      if (!PoolBlocks.TryTake(out block))
        block = new Block(Token);

      block.Header = chain.FetchHeaderDownload();
    }
    finally
    {
      ReleaseLockBlockchain();
    }

    return block;
  }

  void ReorgBlockchain(Blockchain chain)
  {
    Header headerAncestor = chain.HeaderRoot.HeaderPrevious;

    while(BlockchainRoot.tip > )
    BlockchainRoot.RewindTokenToHeight(headerAncestor.Height);

    int height = HeaderTip.Height;

    while (height > heightAncestor)
    {
      BlockLoad.Header = null;
      LoadBlock(height, BlockLoad);

      Token.ReverseBlock(BlockLoad);

      height--;
    }

    try
    {
      RollTokenForwardToTip(heightAncestor);
    }
    catch
    {
      Token = null;

      BlockchainParent.RollTokenForwardToTip(heightAncestor);

      return false;
    }

    chain.SwitchWithRootBranch(headerAncestor);
    BlockchainRoot = chain;

    return true;
  }

  void OnTokenAnchorParent(TXOutputTokenAnchor tokenAnchor)
  {
    try
    {
      if (!TryGetBlockMined(out Block block, tokenAnchor.HashBlockReferenced))
        return;

      BlockchainRoot.AppendHeader(block.Header);

      Token.InsertBlock(block);
      NotifyChildNetworksOfAnchorToken(block);

      BlockchainRoot.HeaderTipBlockchain = block.Header;

      block.WriteToDisk(BlockchainRoot.PathDirectoryBlocks);

      PoolBlocks.Add(block);

      // Hier ein sendBlock machen und intern zuerst header und dann wenn
      // getdata kommt blcok aus peer cache laden, statt wieder node anfragen.
      lock (LOCK_Peers)
        Peers.ForEach(p => HeadersMessage.SendHeaders(
          p,
          new List<byte[]> { block.Header.Hash }));

      // Der User muss jeweils definieren, mit welcher fee Rate er die Verankerung bezahlen will.
      // Dem user kann im GUI auch ein Tool zur verfügung gestellt werden welches ihm 
      // erlaubt, die Fee Rate automatisiert zu steuern. z.B. anhand vergangener Fee Raten
      // oder Marktpreis Arbitrierung.

      if (IsMining)
      {
        block = BlockchainRoot.MineBlock(out TXOutputTokenAnchor anchorToken);

        BlocksMinedCache.Add(block);

        block.WriteToDisk(PathBlocksMined);

        NetworkParent.MineTokenAnchor(tokenAnchor);
      }
    }
    catch (Exception ex)
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
      string pathFileBlock = Path.Combine(PathBlocksMined, block.Header.Hash.ToHexString());

      if (!File.Exists(pathFileBlock))
        return false;

      block = new(Token, File.ReadAllBytes(pathFileBlock));
      block.Parse();
    }

    return block.Header.HashPrevious.IsAllBytesEqual(BlockchainRoot.HeaderTipBlockchain.Hash);
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
    if (!await TryLockBlockchain(10000))
      return (headers: new(), heightAncestor: -1);

    try
    {
      return BlockchainRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task GetBlock(byte[] hash, Block blockUpload)
  {
    if (!await TryLockBlockchain(10000))
      return;

    try
    {
      BlockchainRoot.GetBlock(hash, blockUpload);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}