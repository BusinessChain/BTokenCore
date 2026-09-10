using System.Diagnostics;

namespace BTokenCore;

internal class Blockchain
{
  Blockchain BlockchainParent;
  internal List<Blockchain> BlockchainBranches = new();

  internal Header HeaderTip;
  internal Header HeaderRoot;
  internal Header HeaderTipBlockchain;

  Dictionary<byte[], Header> HeadersAwaitingBlock = new(new EqualityComparerByteArray());
  Header HeaderDownloadNext;

  const int CAPACITY_MAX_QueueBlocksInsertion = 20;
  internal Dictionary<int, Block> QueueBlocks = new();


  internal Blockchain(Header headerGenesis)
  {
    HeaderRoot = headerGenesis;
    HeaderTip = headerGenesis;
  }

  Blockchain(Blockchain blockchainParent, Header headerRoot, Header headerTip)
  {
    BlockchainParent = blockchainParent;
    HeaderRoot = headerRoot;
    HeaderTip = headerTip;
  }

  internal Blockchain TryExtendHeaderchain(Header headerRoot)
  {
    if (TryFindHeaderchain(ref headerRoot, out Blockchain chain, out Header headerAncestor))
      if (chain.HeaderTip == headerAncestor)
        chain.AppendHeader(headerRoot);
      else
      {
        Header headerTip = headerRoot.AppendToHeader(headerAncestor);
        chain = new(this, headerRoot, headerTip);
        chain.BlockchainBranches.Add(chain);
      }

    return chain;
  }

  internal Header GetHeader(byte[] hash)
  {
    Header header = HeaderTip;

    while (header != null && !header.Hash.IsAllBytesEqual(hash))
      header = header.HeaderPrevious;

    return header;
  }

  bool TryFindHeaderchain(
    ref Header headerRoot,
    out Blockchain chain,
    out Header headerAncestor)
  {
    headerAncestor = HeaderTip;

    while (!headerAncestor.Hash.IsAllBytesEqual(headerRoot.HashPrevious))
    {
      if (headerAncestor == HeaderRoot)
      {
        foreach (Blockchain branch in BlockchainBranches)
          if (branch.TryFindHeaderchain(ref headerRoot, out chain, out headerAncestor))
            return true;

        headerAncestor = null;
        chain = null;
        return false;
      }

      headerAncestor = headerAncestor.HeaderPrevious;
    }

    while (headerAncestor.HeaderNext?.Hash.IsAllBytesEqual(headerRoot.Hash) == true)
    {
      headerAncestor = headerAncestor.HeaderNext;

      if (headerRoot.HeaderNext != null)
        headerRoot = headerRoot.HeaderNext;
      else
      {
        headerAncestor = null;
        chain = null;
        return false;
      }
    }

    chain = this;
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

  Blockchain GetRootChain()
  {
    if (BlockchainParent != null)
      return BlockchainParent.GetRootChain();

    return this;
  }

  internal bool TryGetBlockNext(
    out Block block,
    out bool isDirectionForward)
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

  internal Block RollBack()
  {
    QueueBlocks.TryGetValue(HeaderTipBlockchain.Height, out Block block);

    HeaderTipBlockchain = HeaderTipBlockchain.HeaderPrevious;

    return block;
  }

  internal void SwitchWithRootBranch(Blockchain blockchainRootOld)
  {
    HeaderRoot = blockchainRootOld.HeaderRoot;
    blockchainRootOld.HeaderRoot = blockchainRootOld.HeaderTipBlockchain.HeaderNext;

    BlockchainBranches.Add(blockchainRootOld);
    BlockchainParent.BlockchainBranches.Remove(this);

    blockchainRootOld.BlockchainParent = this;
    BlockchainParent = null;
  }

  internal bool IsStrongerThan(Blockchain blockchain)
  {
    return HeaderTipBlockchain.Height >
      blockchain.HeaderTipBlockchain.Height;
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
