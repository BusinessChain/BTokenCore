namespace BTokenCore;

internal partial class Network
{
  SemaphoreSlim SemaphoreBlockchainRoot = new(1);
  internal Blockchain BlockchainRoot;

  string PathBlocksMined = "blocksMined";
  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();

  internal async Task<bool> TryLockBlockchain(int timeout)
  {
    if (NetworkParent != null)
      return await NetworkParent.TryLockBlockchain(timeout);

    return await SemaphoreBlockchainRoot.WaitAsync(timeout).ConfigureAwait(false);
  }

  void ReleaseLockBlockchain()
  {
    if (NetworkParent != null)
      NetworkParent.ReleaseLockBlockchain();
    else
      SemaphoreBlockchainRoot.Release();
  }


  DirectoryInfo DirectoryBlocks;
  

  internal void LoadBlockchain()
  {
    Token.Load();

    DirectoryBlocks = Directory.CreateDirectory("blocksRoot");

    int heightBlockNext = DirectoryBlocks.GetFiles()
    .Select(file => Path.GetFileNameWithoutExtension(file.Name))
    .Where(name => int.TryParse(name, out _))
    .Select(int.Parse)
    .DefaultIfEmpty(0)
    .Min();

    Block blockLoad = new(Token);

    while (true)
      try
      {
        // alle anderen chains sind nur im memory, und gehen bei neustart verloren.
        blockLoad.Header = null;
        LoadBlock(heightBlockNext, blockLoad);

        Token.InsertBlock(blockLoad);

        BlockchainRoot.AppendHeader(blockLoad.Header);

        heightBlockNext += 1;
      }
      catch (Exception ex)
      {
        break;
      }

    if (HeaderRoot == null)
    {
      HeaderRoot = Token.CreateHeaderGenesis();
      HeaderTip = HeaderRoot;
    }
  }

  internal void LoadBlock(int height, Block blockUpload)
  {
    string pathFile = Path.Combine(DirectoryBlocks.FullName, height.ToString());

    using FileStream fileBlock = File.OpenRead(pathFile);

    if (fileBlock.Length > blockUpload.Buffer.Length)
      throw new InvalidOperationException("Block too large for buffer.");

    blockUpload.LengthDataPayload = (int)fileBlock.Length;

    int offset = 0;
    while (offset < blockUpload.LengthDataPayload)
    {
      int n = fileBlock.Read(
          blockUpload.Buffer,
          offset,
          blockUpload.LengthDataPayload - offset);

      if (n == 0)
        throw new EndOfStreamException();

      offset += n;
    }

    blockUpload.Parse();
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

  internal List<Block> PoolBlocks = new();

  internal async Task<Block> InsertBlock(Block block)
  {
    if (await TryLockBlockchain(10000))
      try
      {
        Blockchain chain = BlockchainRoot.FindChain(block);

        if (chain == null)
          return block;

        do
        {
          if (chain == BlockchainRoot)
          {
            Token.InsertBlock(block);
            NotifyChildNetworksOfAnchorToken(block);
          }

          chain.HeaderTipBlockchain = block.Header;

          block.WriteToDisk(chain.PathDirectoryBlocks);

          PoolBlocks.Add(block);

        } while (chain.QueueBlocks.TryGetValue(chain.HeaderTipBlockchain.Height + 1, out block));

        if (chain.IsStrongerThan(BlockchainRoot))
        {
          Header headerAncestor = chain.HeaderRoot.HeaderPrevious;

          if (TryReorgToken(headerAncestor.Height))
            chain.SwitchWithParentBranch(headerAncestor);
        }

        block = TakeFromBlockPool();

        block.Header = chain.FetchHeaderDownload();
      }
      finally
      {
        ReleaseLockBlockchain();
      }

    return block;
  }

  bool TryReorgToken(int heightAncestor)
  {
    BlockchainRoot.RewindTokenToHeight(heightAncestor);

    Token = BlockchainParent.Token;

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

    BlockchainParent.Token = null;

    return true;
  }

  Block TakeFromBlockPool()
  {
    if (!PoolBlocks.Any())
      return new(Token);

    Block block = PoolBlocks[0];
    PoolBlocks.RemoveAt(0);

    return block;
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