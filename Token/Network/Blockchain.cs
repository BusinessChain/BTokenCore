namespace BTokenCore;


internal class Blockchain
{
  Blockchain BlockchainParent;
  List<Blockchain> BlockchainBranches = new();

  Header HeaderTip;
  internal Header HeaderRoot;
  internal Header HeaderTipBlockchain;

  internal string PathDirectoryBlocks;
  DirectoryInfo DirectoryBlocks;

  Dictionary<byte[], Header> HeadersDownloading = new(new EqualityComparerByteArray());
  Header HeaderDownloadNext;

  const int CAPACITY_MAX_QueueBlocksInsertion = 20;
  internal Dictionary<int, Block> QueueBlocks = new();

  Block BlockLoad;


  internal Blockchain(IToken token)
  {
    BlockLoad = new Block(token);

    DirectoryBlocks = Directory.CreateDirectory("blocksRoot");
  }

  Blockchain(Blockchain blockchainParent, Header headerRoot, Header headerTip)
  {
    BlockchainParent = blockchainParent;
    HeaderRoot = headerRoot;
    HeaderTip = headerTip;

    string pathDirectory = Path.Combine(
      blockchainParent.DirectoryBlocks.FullName,
      "branch" + blockchainParent.BlockchainBranches.Count.ToString());

    DirectoryBlocks = Directory.CreateDirectory(pathDirectory);
  }

  internal int GetHeight()
  {
    return HeaderTip.Height;
  }

  internal bool TryExtendHeaderchain(
    Header header,
    out List<byte[]> locator,
    Block blockDownload)
  {
    locator = null;

    if (header == null)
      return false;

    Header headerAncestor = HeaderTip;

    while (!headerAncestor.Hash.IsAllBytesEqual(header.HashPrevious))
    {
      if (headerAncestor == HeaderRoot)
      {
        foreach (Blockchain sync in BlockchainBranches)
          if (sync.TryExtendHeaderchain(header, out locator, blockDownload))
            return true;

        locator = GetLocator();
        return false;
      }

      headerAncestor = headerAncestor.HeaderPrevious;
    }

    while (headerAncestor != HeaderTip)
    {
      if (headerAncestor.HeaderNext.Hash.IsAllBytesEqual(header.Hash) == false)
      {
        foreach (Blockchain sync in BlockchainBranches)
          if (sync.HeaderRoot.Hash.IsAllBytesEqual(header.Hash))
            return sync.TryExtendHeaderchain(header.HeaderNext, out locator, blockDownload);

        Header headerTip = header.AppendToHeader(headerAncestor);
        Blockchain syncBranch = new(this, header, headerTip);
        BlockchainBranches.Add(syncBranch);

        blockDownload.Header = syncBranch.FetchHeaderDownload();
        locator = new List<byte[]> { headerTip.Hash };
        return false;
      }

      if (header.HeaderNext == null)
      {
        blockDownload.Header = FetchHeaderDownload();
        locator = null;
        return false;
      }

      headerAncestor = headerAncestor.HeaderNext;
      header = header.HeaderNext;
    }

    AppendHeader(header);

    blockDownload.Header = FetchHeaderDownload();

    locator = new List<byte[]> { HeaderTip.Hash };
    return true;
  }

  internal void AppendHeader(Header header)
  {
    Header headerTipNew = header.AppendToHeader(HeaderTip);
    HeaderTip.HeaderNext = header;
    HeaderTip = headerTipNew;
  }

  internal Header FetchHeaderDownload()
  {
    if ((QueueBlocks.Count > CAPACITY_MAX_QueueBlocksInsertion || HeaderDownloadNext == null)
        && HeadersDownloading.Any())
      return HeadersDownloading.Values.MinBy(h => h.Height);

    if (HeaderDownloadNext != null)
    {
      Header headerDownload = HeaderDownloadNext;
      HeadersDownloading.Add(headerDownload.Hash, headerDownload);
      HeaderDownloadNext = HeaderDownloadNext.HeaderNext;
      return headerDownload;
    }

    return null;
  }

  internal Block MineBlock(out TXOutputTokenAnchor anchorToken)
  {
    int height = HeaderTip.Height + 1;

    Block block = Token.MineBlock(height, out anchorToken);

    block.Header.HashPrevious = HeaderTip.Hash;

    block.Header.ComputeHash();

    block.Serialize();

    return block;
  }

  internal Blockchain FindChain(Block block)
  {
    Header header = block.Header;

    if (HeadersDownloading.Remove(header.Hash))
    {
      if (header.Height == HeaderTipBlockchain.Height + 1)
        return this;

      QueueBlocks.Add(header.Height, block);
      return null;
    }

    foreach (Blockchain branch in BlockchainBranches)
    {
      Blockchain blockchain = branch.FindChain(block);

      if (blockchain != null)
        return blockchain;
    }

    return null;
  }

  bool TryReorg()
  {
    if (IsRoot() || !IsStrongerThan(BlockchainParent))
      return false;

    Header headerAncestor = HeaderRoot.HeaderPrevious;

    if (BlockchainParent.IsRoot() && !TryReorgToken(headerAncestor.Height))
      return false;

    SwitchWithParentBranch(headerAncestor);

    if (!IsRoot())
      return TryReorg();

    return true;
  }

  bool TryReorgToken(int heightAncestor)
  {
    BlockchainParent.RewindTokenToHeight(heightAncestor);

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

  internal void RewindTokenToHeight(int heightAncestor)
  {
    int height = HeaderTip.Height;

    while (height > heightAncestor)
    {
      BlockLoad.Header = null;
      LoadBlock(height, BlockLoad);

      Token.ReverseBlock(BlockLoad);

      height--;
    }
  }

  internal void RollTokenForwardToTip(int heightAncestor)
  {
    int height = heightAncestor + 1;

    while (height <= HeaderTip.Height)
    {
      BlockLoad.Header = null;
      LoadBlock(height, BlockLoad);

      Token.InsertBlock(BlockLoad);

      height++;
    }
  }

  internal void SwitchWithParentBranch(Header headerAncestor)
  {
    Header headerRootNewSyncParent = headerAncestor.HeaderNext;
    headerAncestor.HeaderNext = HeaderRoot;
    HeaderRoot = BlockchainParent.HeaderRoot;
    BlockchainParent.HeaderRoot = headerRootNewSyncParent;

    List<Blockchain> branches = BlockchainParent.BlockchainBranches.ToList();

    foreach (Blockchain syncBranch in branches)
      if (syncBranch.HeaderRoot.Height <= HeaderRoot.Height)
      {
        BlockchainParent.BlockchainBranches.Remove(syncBranch);

        if (syncBranch != this)
        {
          syncBranch.BlockchainParent = this;
          BlockchainBranches.Add(syncBranch);
        }
      }

    BlockchainBranches.Add(BlockchainParent);

    Blockchain syncParentNew = BlockchainParent.BlockchainParent;
    BlockchainParent.BlockchainParent = this;
    BlockchainParent = syncParentNew;
  }

  bool IsRoot()
  {
    return BlockchainParent == null;
  }

  internal bool IsStrongerThan(Blockchain blockchain)
  {
    return HeaderTipBlockchain.DifficultyAccumulated > 
      blockchain.HeaderTipBlockchain.DifficultyAccumulated;
  }

  internal void GetBlock(byte[] hash, Block blockUpload)
  {
    Header header = HeaderRoot;

    while (header != null)
    {
      if (header.Hash.IsAllBytesEqual(hash))
      {
        blockUpload.Header = header;
        LoadBlock(header.Height, blockUpload);
        return;
      }

      header = header.HeaderNext;
    }

    foreach (Blockchain syncBranch in BlockchainBranches)
    {
      syncBranch.GetBlock(hash, blockUpload);

      if (blockUpload.Header != null)
        return;
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

  internal List<byte[]> GetLocator()
  {
    Header header = HeaderTip;
    List<byte[]> locator = new();
    int depth = 0;
    int nextLocationDepth = 0;

    while (header != null)
    {
      if (depth == nextLocationDepth || header.HeaderPrevious == null)
      {
        locator.Add(header.Hash);
        nextLocationDepth = 2 * nextLocationDepth + 1;
      }

      depth++;
      header = header.HeaderPrevious;
    }

    return locator;
  }

  internal (List<byte[]> headers, int heightAncestor) GetHeadersSerialized(
    List<byte[]> hashesLocator,
    int maxCountHeaders)
  {
    Header header = HeaderTip;

    while (header != null)
    {
      foreach (byte[] hashLocator in hashesLocator)
        if (header.Hash.IsAllBytesEqual(hashLocator))
          goto LABEL_HeaderAncestorFound;

      header = header.HeaderPrevious;
    }

    return (headers: new(), heightAncestor: -1);

  LABEL_HeaderAncestorFound:

    List<byte[]> headers = new();
    int heightAncestor = header.Height;

    while (header.HeaderNext != null && headers.Count < maxCountHeaders)
    {
      headers.Add(header.HeaderNext.Serialize());
      header = header.HeaderNext;
    }

    return (headers, heightAncestor);
  }
}
