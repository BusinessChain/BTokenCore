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

  internal static bool TrySearchHeaderAncestor(
    byte[] hashPrevious,
    out Header headerAncestor,
    ref Blockchain chain)
  {
    headerAncestor = chain.HeaderTip;

    while (!headerAncestor.Hash.IsAllBytesEqual(hashPrevious))
    {
      if (headerAncestor == chain.HeaderRoot)
      {
        foreach(Blockchain branch in chain.BlockchainBranches)
        {
          chain = branch;

          if (TrySearchHeaderAncestor(hashPrevious, out headerAncestor, ref chain))
            return true;
        }

        return false;
      }

      headerAncestor = headerAncestor.HeaderPrevious;
    }

    return true;
  }

  internal (byte[] headerTipChainHash, byte[] hashBlockNextDownload)
    TryExtendHeaderchain(List<Header> headers)
  {
    Blockchain chain = this;
    Header headerAncestor;

    if(!TrySearchHeaderAncestor(headers[0].HashPrevious, out headerAncestor, ref chain))
      return (null, null);

    while (headerAncestor.HeaderNext?.Hash.IsAllBytesEqual(headers[0].Hash) == true)
    {
      headers.RemoveAt(0);

      if (headers.Count == 0)
        return (null, null);

      headerAncestor = headerAncestor.HeaderNext;
    }

    if (headerAncestor != chain.HeaderTip)
    {
      headers[0].AppendToHeader(headerAncestor);

      Blockchain branch = new(this, headers[0]);
      chain.BlockchainBranches.Add(branch);
      chain = branch;
    }

    for (int i = 1; i < headers.Count; i++)
      chain.AppendHeader(headers[i]);

    byte[] hashBlockNextDownload = null;

    if (chain.HeaderTip.Height > GetRootChain().HeaderTipBlockchain.Height)
    {
      if(chain.HeaderTipBlockchain == null)
        hashBlockNextDownload = chain.ro // Chains stufenweise promoten
      hashBlockNextDownload = chain.HeaderTipBlockchain.HeaderNext.Hash;
    }

    return (chain.HeaderTip.Hash, hashBlockNextDownload);
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

  internal Header FetchHeaderDownload()
  {
    if ((QueueBlocks.Count > CAPACITY_MAX_QueueBlocksInsertion || HeaderDownloadNext == null)
        && HeadersAwaitingBlock.Any())
      return HeadersAwaitingBlock.Values.MinBy(h => h.Height);

    if (HeaderDownloadNext != null)
    {
      Header headerDownload = HeaderDownloadNext;
      HeadersAwaitingBlock.Add(headerDownload.Hash, headerDownload);
      HeaderDownloadNext = HeaderDownloadNext.HeaderNext;
      return headerDownload;
    }

    return null;
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

    if (BlockchainParent == null)
      throw new ProtocolException(
        $"Received block {block} but header in blockchain not found.");

    return null;
  }

  internal bool TryGetBlockNext( out Block block, out bool isDirectionForward)
  {
    Blockchain blockchainRoot = GetRootChain();
    isDirectionForward = true;

    while(QueueBlocks.TryGetValue(HeaderTipBlockchain.Height + 1, out block))
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

  bool TryFindHeaderchain(
    List<Header> headers,
    out Blockchain chain,
    out Header headerAncestor)
  {
    chain = this;
    headerAncestor = HeaderTip;

    while (!headerAncestor.Hash.IsAllBytesEqual(headers[0].HashPrevious))
    {
      if (headerAncestor == HeaderRoot)
      {
        foreach (Blockchain branch in BlockchainBranches)
          if (branch.TryFindHeaderchain(headers, out chain, out headerAncestor))
            return true;

        headerAncestor = null;
        chain = null;
        return false;
      }

      headerAncestor = headerAncestor.HeaderPrevious;
    }

    while (headerAncestor.HeaderNext?.Hash.IsAllBytesEqual(headers[0].Hash) == true)
    {
      headers.RemoveAt(0);
      headerAncestor = headerAncestor.HeaderNext;

      if (headers.Count == 0)
      {
        headerAncestor = null;
        chain = null;
        return false;
      }
    }

    return true;
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
