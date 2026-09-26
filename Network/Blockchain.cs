namespace BTokenCore;

internal class Blockchain
{
  internal Header HeaderTip;
  internal Header HeaderRoot;
  internal Header HeaderTipBlockchain;

  Blockchain BlockchainParent;
  List<Blockchain> BlockchainBranches = new();

  Dictionary<byte[], Header> HeadersAwaitingBlock = new(new EqualityComparerByteArray());
  Header HeaderDownloadNext;

  const int CAPACITY_MAX_QueueBlocksInsertion = 20;
  Dictionary<int, Block> QueueBlocks = new();


  internal Blockchain(Header headerGenesis)
    : this(null, headerGenesis)
  { }

  internal Header TryExtendHeaderchain(List<Header> headers)
  {
    Blockchain chain = this;
    Header headerAncestor;

    if (!TrySearchHeaderAncestor(headers, out headerAncestor, ref chain))
      return null;

    if (headers.Count == 0)
      return headerAncestor;

    if (headerAncestor != chain.HeaderTip)
    {
      headers[0].AppendToHeader(headerAncestor);

      Blockchain branch = new(chain, headers[0]);
      chain.BlockchainBranches.Add(branch);
      chain = branch;
    }

    for (int i = 1; i < headers.Count; i++)
      chain.AppendHeader(headers[i]);

    while (chain.BlockchainParent?.BlockchainParent != null
      && chain.BlockchainParent.HeaderTip.Height < chain.HeaderTip.Height)
      chain.Promote();

    return headers[^1];
  }

  internal static bool TrySearchHeaderAncestor(
    List<Header> headers,
    out Header headerAncestor,
    ref Blockchain chain)
  {
    headerAncestor = chain.HeaderTip;

    while (!headerAncestor.Hash.IsAllBytesEqual(headers[0].HashPrevious))
    {
      if (headerAncestor == chain.HeaderRoot)
      {
        foreach (Blockchain branch in chain.BlockchainBranches)
        {
          chain = branch;

          if (TrySearchHeaderAncestor(headers, out headerAncestor, ref chain))
            return true;
        }

        return false;
      }

      headerAncestor = headerAncestor.HeaderPrevious;
    }

    while (headerAncestor.HeaderNext?.Hash.IsAllBytesEqual(headers[0].Hash) == true)
    {
      headerAncestor = headerAncestor.HeaderNext;
      headers.RemoveAt(0);

      if (headers.Count == 0)
        break;
    }

    return true;
  }
   
  internal void Promote()
  {
    Blockchain chainParent = BlockchainParent;
    Header headerAncestor = HeaderRoot.HeaderPrevious;
    Header headerRootParentNew = headerAncestor.HeaderNext;

    headerAncestor.HeaderNext = HeaderRoot;
    HeaderRoot = chainParent.HeaderRoot;
    chainParent.HeaderRoot = headerRootParentNew;

    List<Blockchain> branchesGrandparent = chainParent.BlockchainParent.BlockchainBranches;
    branchesGrandparent[branchesGrandparent.IndexOf(chainParent)] = this;
    BlockchainParent = chainParent.BlockchainParent;

    chainParent.BlockchainBranches.Remove(this);
    chainParent.BlockchainParent = this;

    foreach (Blockchain branch in chainParent.BlockchainBranches
      .Where(b => b.HeaderRoot.HeaderPrevious.Height <= headerAncestor.Height).ToList())
    {
      chainParent.BlockchainBranches.Remove(branch);
      BlockchainBranches.Add(branch);
      branch.BlockchainParent = this;
    }

    BlockchainBranches.Add(chainParent);

    foreach (Header header in chainParent.HeadersAwaitingBlock.Values
      .Where(h => h.Height <= headerAncestor.Height).ToList())
    {
      chainParent.HeadersAwaitingBlock.Remove(header.Hash);
      HeadersAwaitingBlock.Add(header.Hash, header);
    }

    foreach (int height in chainParent.QueueBlocks.Keys
      .Where(h => h <= headerAncestor.Height).ToList())
    {
      QueueBlocks.Add(height, chainParent.QueueBlocks[height]);
      chainParent.QueueBlocks.Remove(height);
    }

    // HeaderTipBlockchain und evt. HeaderTipBlockchain müssen im Parent allenfalls auf null gesetzt werden.
    if (chainParent.HeaderTipBlockchain.Height < headerAncestor.Height)
      HeaderTipBlockchain = chainParent.HeaderTipBlockchain;

    if (chainParent.HeaderDownloadNext?.Height <= headerAncestor.Height)
    {
      HeaderDownloadNext = chainParent.HeaderDownloadNext;
      chainParent.HeaderDownloadNext = chainParent.HeaderRoot;
    }
  }

  internal Header GetHeader(byte[] hash)
  {
    Header header = HeaderTip;

    while (header != null && !header.Hash.IsAllBytesEqual(hash))
      header = header.HeaderPrevious;

    return header;
  }

  internal void AppendHeader(Header header)
  {
    header.AppendToHeader(HeaderTip);

    HeaderTip.HeaderNext = header;
    HeaderTip = header;
  }

  internal Blockchain FindChain(Header header)
  {
    if (HeaderRoot.Height <= header.Height && header.Height <= HeaderTip.Height)
    {
      Header headerInChain = HeaderTip;

      while (headerInChain.Height > header.Height)
        headerInChain = headerInChain.HeaderPrevious;

      if (headerInChain == header)
        return this;
    }

    foreach (Blockchain branch in BlockchainBranches)
      if (branch.FindChain(header) is Blockchain chain)
        return chain;

    return null;
  }

  internal Header FetchHeaderDownloadAlongPath(int heightMax)
  {
    return FetchAlongPath(heightMax, (chain, height) => chain.FetchHeaderDownload(height))
      ?? FetchAlongPath(heightMax, (chain, height) => chain.GetHeaderAwaitingBlockLowest(height));
  }

  Header FetchAlongPath(int heightMax, Func<Blockchain, int, Header> fetch)
  {
    if (BlockchainParent?.FetchAlongPath(HeaderRoot.Height - 1, fetch) is Header header)
      return header;

    return fetch(this, heightMax);
  }

  Header FetchHeaderDownload(int heightMax)
  {
    if (QueueBlocks.Count > CAPACITY_MAX_QueueBlocksInsertion
      || HeaderDownloadNext == null
      || HeaderDownloadNext.Height > heightMax)
      return null;

    Header headerDownload = HeaderDownloadNext;
    HeadersAwaitingBlock.Add(headerDownload.Hash, headerDownload);
    HeaderDownloadNext = HeaderDownloadNext.HeaderNext;
    return headerDownload;
  }

  Header GetHeaderAwaitingBlockLowest(int heightMax)
  {
    return HeadersAwaitingBlock.Values
      .Where(h => h.Height <= heightMax)
      .MinBy(h => h.Height);
  }

  internal Blockchain InsertBlockInChain(Block block)
  {
    if (HeadersAwaitingBlock.Remove(block.Header.Hash))
    {
      QueueBlocks.Add(block.Header.Height, block);
      return this;
    }

    foreach (Blockchain branch in BlockchainBranches)
      if (branch.InsertBlockInChain(block) is Blockchain chain)
        return chain;

    return null;
  }

  internal bool TryGetBlockNext( out Block block, out bool isDirectionForward)
  {
    Blockchain blockchainRoot = GetRootChain();
    isDirectionForward = true;

    while(QueueBlocks.Remove(HeaderTipBlockchain.Height + 1, out block))
    {
      HeaderTipBlockchain = block.Header;

      if (this == blockchainRoot)
        return true;

      if (IsStrongerThan(blockchainRoot))
      {
        if (blockchainRoot.HeaderTipBlockchain.Height > HeaderRoot.Height - 1)
        {
          block = blockchainRoot.RollBack();
          isDirectionForward = false;

          return true;
        }

        SwitchWithRootBranch(blockchainRoot);
      }
    }

    return false;
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


  Blockchain(Blockchain blockchainParent, Header headerRoot)
  {
    BlockchainParent = blockchainParent;
    HeaderRoot = headerRoot;
    HeaderTip = headerRoot;
    HeaderDownloadNext = headerRoot;
  }

  Blockchain GetRootChain()
  {
    if (BlockchainParent != null)
      return BlockchainParent.GetRootChain();

    return this;
  }

  Block RollBack()
  {
    QueueBlocks.TryGetValue(HeaderTipBlockchain.Height, out Block block);

    HeaderTipBlockchain = HeaderTipBlockchain.HeaderPrevious;

    return block;
  }

  void SwitchWithRootBranch(Blockchain blockchainRootOld)
  {
    HeaderRoot = blockchainRootOld.HeaderRoot;
    blockchainRootOld.HeaderRoot = blockchainRootOld.HeaderTipBlockchain.HeaderNext;

    BlockchainBranches.Add(blockchainRootOld);
    BlockchainParent.BlockchainBranches.Remove(this);

    blockchainRootOld.BlockchainParent = this;
    BlockchainParent = null;
  }

  bool IsStrongerThan(Blockchain blockchain)
  {
    return HeaderTipBlockchain.Height >
      blockchain.HeaderTipBlockchain.Height;
  }
}
