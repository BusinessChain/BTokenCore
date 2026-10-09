namespace BTokenCore;

internal partial class Blockchain
{
  class Chain
  {
    internal Header HeaderTip;
    internal Header HeaderRoot;
    internal Header HeaderTipBlockchain;

    internal Chain ChainParent;
    List<Chain> ChainsChild = new();

    Dictionary<byte[], Header> HeadersAwaitingBlock = new(new EqualityComparerByteArray());

    const int DEPTH_MAX_BlockMissing = 20;
    internal Dictionary<int, Block> Blocks = new();


    internal Chain(Header headerGenesis, bool isRoot)
      : this(null, headerGenesis, isRoot)
    { }

    Chain(Chain blockchainParent, Header headerRoot, bool isRoot)
    {
      ChainParent = blockchainParent;
      HeaderRoot = headerRoot;
      HeaderTip = headerRoot;

      if (isRoot)
        HeaderTipBlockchain = headerRoot;
    }

    internal void InsertHeadersInTree(List<Header> headers, out Header headerLastInTree)
    {
      Chain chain = this;
      headerLastInTree = null;

      if (!TrySearchHeaderAncestor(headers, ref chain, out Header headerAncestor))
        return;

      if (headers.Count == 0)
      {
        headerLastInTree = headerAncestor;
        return;
      }

      if (headerAncestor != chain.HeaderTip)
      {
        if (!headers[0].TryAppendToHeader(headerAncestor))
          return;

        Chain chainChild = new(chain, headers[0], isRoot: false);
        chain.ChainsChild.Add(chainChild);
        chain = chainChild;
      }
      else
        if (!chain.TryAppendHeader(headers[0]))
          return;

      for (int i = 1; i < headers.Count; i++)
        if (!chain.TryAppendHeader(headers[i]))
          return;

      headerLastInTree = headers[^1];
    }

    static bool TrySearchHeaderAncestor(List<Header> headers, ref Chain chain, out Header headerAncestor)
    {
      headerAncestor = chain.HeaderTip;

      while (!headerAncestor.Hash.IsAllBytesEqual(headers[0].HashPrevious))
      {
        if (headerAncestor == chain.HeaderRoot)
        {
          foreach (Chain chainChild in chain.ChainsChild)
          {
            chain = chainChild;

            if (TrySearchHeaderAncestor(headers, ref chain, out headerAncestor))
              return true;
          }

          return false;
        }

        headerAncestor = headerAncestor.HeaderPrevious;
      }

      while (headers.Count > 0)
      {
        if (headerAncestor.HeaderNext?.Hash.IsAllBytesEqual(headers[0].Hash) == true)
          headerAncestor = headerAncestor.HeaderNext;
        else
          foreach (Chain chainChild in chain.ChainsChild)
            if (chainChild.HeaderRoot.HeaderPrevious == headerAncestor
              && chainChild.HeaderRoot.Hash.IsAllBytesEqual(headers[0].Hash))
            {
              chain = chainChild;
              headerAncestor = chainChild.HeaderRoot;
              break;
            }

        if (!headerAncestor.Hash.IsAllBytesEqual(headers[0].Hash))
          break;

        headers.RemoveAt(0);
      }

      return true;
    }

    internal bool TryGetHeader(byte[] hash, out Header header)
    {
      header = HeaderTip;

      while (header != null && !header.Hash.IsAllBytesEqual(hash))
        header = header.HeaderPrevious;

      return header != null;
    }

    internal bool TryGetHeaderPlaceholder(Header headerParent, out Header headerPlaceholder)
    {
      Header headerFork = HeaderRoot.HeaderPrevious;
      headerPlaceholder = HeaderTip;

      while (headerPlaceholder != headerFork && headerPlaceholder.HeaderParent != headerParent)
        headerPlaceholder = headerPlaceholder.HeaderPrevious;

      if (headerPlaceholder != headerFork)
        return true;

      foreach (Chain chainChild in ChainsChild)
        if (chainChild.TryGetHeaderPlaceholder(headerParent, out headerPlaceholder))
          return true;

      return false;
    }

    internal void RemoveHeaderTip()
    {
      Header headerPrevious = HeaderTip.HeaderPrevious;

      HeadersAwaitingBlock.Remove(HeaderTip.Hash);

      if (HeaderTipBlockchain == HeaderTip)
        HeaderTipBlockchain = headerPrevious;

      if (HeaderTip == HeaderRoot)
        ChainParent.ChainsChild.Remove(this);
      else
        headerPrevious.HeaderNext = null;

      HeaderTip = headerPrevious;
    }

    internal bool TryAppendHeader(Header header)
    {
      if (!header.TryAppendToHeader(HeaderTip))
        return false;

      HeaderTip.HeaderNext = header;
      HeaderTip = header;
      return true;
    }

    internal Chain FindChain(Header header)
    {
      if (ContainsHeader(header))
        return this;

      foreach (Chain chainChild in ChainsChild)
        if (chainChild.FindChain(header) is Chain chain)
          return chain;

      return null;
    }

    bool ContainsHeader(Header header)
    {
      if (header.Height < HeaderRoot.Height || HeaderTip.Height < header.Height)
        return false;

      Header headerInChain = HeaderTip;

      while (headerInChain.Height > header.Height)
        headerInChain = headerInChain.HeaderPrevious;

      return headerInChain == header;
    }

    internal Header FetchHeaderBlockMissingNextInChain(int heightTarget, Func<Header, bool> canSupplierDeliverBlock)
    {
      bool isChainParentLagging = ChainParent != null
        && (ChainParent.HeaderTipBlockchain == null
          || ChainParent.HeaderTipBlockchain.Height < HeaderRoot.Height - 1);

      if (isChainParentLagging)
        return ChainParent.FetchHeaderBlockMissingNextInChain(HeaderRoot.Height - 1, canSupplierDeliverBlock);

      int heightBlockNext = HeaderTipBlockchain != null
        ? HeaderTipBlockchain.Height + 1 : HeaderRoot.Height;

      Header headerCandidate = heightBlockNext == HeaderRoot.Height
        ? HeaderRoot : HeaderTipBlockchain.HeaderNext;

      while (headerCandidate != null
        && headerCandidate.Height <= heightTarget
        && headerCandidate.Height <= heightBlockNext + DEPTH_MAX_BlockMissing)
      {
        if (!Blocks.ContainsKey(headerCandidate.Height)
          && canSupplierDeliverBlock(headerCandidate)
          && HeadersAwaitingBlock.TryAdd(headerCandidate.Hash, headerCandidate))
          return headerCandidate;

        headerCandidate = headerCandidate.HeaderNext;
      }

      return HeadersAwaitingBlock.Values
        .Where(h => h.Height <= heightTarget && canSupplierDeliverBlock(h))
        .MinBy(h => h.Height);
    }

    internal Header FetchHeaderBlockMissingNextInTree(int heightTipBlockchainRoot, Func<Header, bool> canSupplierDeliverBlock)
    {
      bool isChainAhead = HeaderTip.Height > heightTipBlockchainRoot;

      if (isChainAhead
        && FetchHeaderBlockMissingNextInChain(HeaderTip.Height, canSupplierDeliverBlock) is Header header)
        return header;

      foreach (Chain chainChild in ChainsChild)
        if (chainChild.FetchHeaderBlockMissingNextInTree(heightTipBlockchainRoot, canSupplierDeliverBlock) is Header headerChild)
          return headerChild;

      return null;
    }

    internal bool TryQueueBlock(Block block, out Chain chainQueued)
    {
      bool isHeaderOfBlockAwaitedHere = HeadersAwaitingBlock.Remove(block.Header.Hash);

      if (isHeaderOfBlockAwaitedHere)
      {
        Blocks.Add(block.Header.Height, block);
        chainQueued = this;
        return true;
      }

      foreach (Chain chain in ChainsChild)
        if (chain.TryQueueBlock(block, out chainQueued))
          return true;

      chainQueued = null;
      return false;
    }

    internal void AdvanceTipChain()
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

    internal void SwitchWithParent()
    {
      Chain chainParent = ChainParent;
      Header headerFork = HeaderRoot.HeaderPrevious;
      int heightFork = headerFork.Height;

      (Blocks, chainParent.Blocks) = (chainParent.Blocks, Blocks);
      (HeadersAwaitingBlock, chainParent.HeadersAwaitingBlock) = (chainParent.HeadersAwaitingBlock, HeadersAwaitingBlock);
      (ChainsChild, chainParent.ChainsChild) = (chainParent.ChainsChild, ChainsChild);

      MoveItems(Blocks, chainParent.Blocks, b => b.Key <= heightFork);
      MoveItems(HeadersAwaitingBlock, chainParent.HeadersAwaitingBlock, h => h.Value.Height <= heightFork);
      MoveItems(ChainsChild, chainParent.ChainsChild, c => c.HeaderRoot.HeaderPrevious.Height <= heightFork);

      foreach (Chain chainChild in chainParent.ChainsChild)
        chainChild.ChainParent = chainParent;

      foreach (Chain chainChild in ChainsChild)
        chainChild.ChainParent = this;

      Header headerRootChild = headerFork.HeaderNext;
      headerFork.HeaderNext = HeaderRoot;
      HeaderRoot = headerRootChild;
      (HeaderTip, chainParent.HeaderTip) = (chainParent.HeaderTip, HeaderTip);

      if (chainParent.ChainParent != null)
      {
        chainParent.HeaderTipBlockchain = null;
        chainParent.AdvanceTipChain();
      }

      HeaderTipBlockchain = null;
      AdvanceTipChain();
    }

    static void MoveItems<T>(ICollection<T> source, ICollection<T> target, Func<T, bool> predicate)
    {
      foreach (T item in source.Where(predicate).ToList())
      {
        source.Remove(item);
        target.Add(item);
      }
    }

    internal bool IsStrongerThan(Chain blockchain)
    {
      return HeaderTipBlockchain != null
        && HeaderTipBlockchain.Height > blockchain.HeaderTipBlockchain.Height;
    }
  }
}
