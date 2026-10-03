namespace BTokenCore;

internal partial class Blockchain
{
  class Branch
  {
    internal Header HeaderTip;
    internal Header HeaderRoot;
    internal Header HeaderTipBlockchain;

    Branch BranchParent;
    List<Branch> BranchesChild = new();

    Dictionary<byte[], Header> HeadersAwaitingBlock = new(new EqualityComparerByteArray());
    Header HeaderDownloadNext;

    const int DEPTH_MAX_BlockMissing = 20;
    internal Dictionary<int, Block> Blocks = new();


    internal Branch(Header headerGenesis, bool isRoot)
      : this(null, headerGenesis, isRoot)
    { }

    internal Header TryExtendHeaderchain(List<Header> headers)
    {
      Branch branch = this;

      if (!TrySearchHeaderAncestor(headers, ref branch, out Header headerAncestor))
        return null;

      if (headers.Count == 0)
        return headerAncestor;

      if (headerAncestor != branch.HeaderTip)
      {
        headers[0].AppendToHeader(headerAncestor);

        Branch branchChild = new(branch, headers[0], isRoot: false);
        branch.BranchesChild.Add(branchChild);
        branch = branchChild;
      }
      else
        branch.AppendHeader(headers[0]);

      for (int i = 1; i < headers.Count; i++)
        branch.AppendHeader(headers[i]);

      return headers[^1];
    }

    internal static bool TrySearchHeaderAncestor(
      List<Header> headers,
      ref Branch branch,
      out Header headerAncestor)
    {
      headerAncestor = branch.HeaderTip;

      while (!headerAncestor.Hash.IsAllBytesEqual(headers[0].HashPrevious))
      {
        if (headerAncestor == branch.HeaderRoot)
        {
          foreach (Branch branchChild in branch.BranchesChild)
          {
            branch = branchChild;

            if (TrySearchHeaderAncestor(headers, ref branch, out headerAncestor))
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

    internal Branch FindChain(Header header)
    {
      if (HeaderRoot.Height <= header.Height && header.Height <= HeaderTip.Height)
      {
        Header headerInChain = HeaderTip;

        while (headerInChain.Height > header.Height)
          headerInChain = headerInChain.HeaderPrevious;

        if (headerInChain == header)
          return this;
      }

      foreach (Branch branch in BranchesChild)
        if (branch.FindChain(header) is Branch chain)
          return chain;

      return null;
    }

    internal Header FetchHeaderDownloadAlongPath(int heightMax)
    {
      return FetchAlongPath(heightMax, (chain, height) => chain.FetchHeaderDownload(height))
        ?? FetchAlongPath(heightMax, (chain, height) => chain.GetHeaderAwaitingBlockLowest(height));
    }

    Header FetchAlongPath(int heightMax, Func<Branch, int, Header> fetch)
    {
      if (BranchParent?.FetchAlongPath(HeaderRoot.Height - 1, fetch) is Header header)
        return header;

      return fetch(this, heightMax);
    }

    Header FetchHeaderDownload(int heightMax)
    {
      int heightBlockNext = HeaderTipBlockchain != null
        ? HeaderTipBlockchain.Height + 1 : HeaderRoot.Height;

      if (HeaderDownloadNext == null
        || HeaderDownloadNext.Height > heightMax
        || HeaderDownloadNext.Height - heightBlockNext > DEPTH_MAX_BlockMissing)
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

    internal bool TryQueueBlock(Block block, out Branch branchQueued)
    {
      if (HeadersAwaitingBlock.Remove(block.Header.Hash))
      {
        Blocks.Add(block.Header.Height, block);
        branchQueued = this;
        return true;
      }

      foreach (Branch branch in BranchesChild)
        if (branch.TryQueueBlock(block, out branchQueued))
          return true;

      branchQueued = null;
      return false;
    }

    internal Branch QueueBlockMined(Block block)
    {
      TryExtendHeaderchain(new List<Header> { block.Header });

      if (FindChain(block.Header) is not Branch chain)
        return null;

      if (chain.HeaderDownloadNext == block.Header)
        chain.HeaderDownloadNext = block.Header.HeaderNext;

      chain.Blocks.Add(block.Header.Height, block);

      return chain;
    }

    internal void AdvanceTipBlockchain()
    {
      int heightBlockNext = HeaderTipBlockchain != null
        ? HeaderTipBlockchain.Height + 1 : HeaderRoot.Height;

      while (Blocks.TryGetValue(heightBlockNext, out Block block))
      {
        HeaderTipBlockchain = block.Header;
        heightBlockNext += 1;
      }
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


    Branch(Branch blockchainParent, Header headerRoot, bool isRoot)
    {
      BranchParent = blockchainParent;
      HeaderRoot = headerRoot;
      HeaderTip = headerRoot;

      if (isRoot)
        HeaderTipBlockchain = headerRoot;

      HeaderDownloadNext = headerRoot;
    }

    internal void SwitchWithRootBranch(Branch branchRootOld)
    {
      HeaderRoot = branchRootOld.HeaderRoot;
      branchRootOld.HeaderRoot = branchRootOld.HeaderTipBlockchain.HeaderNext;

      BranchesChild.Add(branchRootOld);
      BranchParent.BranchesChild.Remove(this);

      branchRootOld.BranchParent = this;
      BranchParent = null;
    }

    internal bool IsStrongerThan(Branch blockchain)
    {
      return HeaderTipBlockchain != null
        && HeaderTipBlockchain.Height > blockchain.HeaderTipBlockchain.Height;
    }
  }
}
