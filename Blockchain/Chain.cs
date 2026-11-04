namespace BTokenCore;

internal partial class Blockchain
{
  protected class Chain
  {
    internal Header HeaderTip;
    internal Header HeaderRoot;
    internal Header HeaderTipBlockchain;

    internal Chain ChainParent;
    List<Chain> ChainsChild = new();

    internal Dictionary<byte[], Header> HeadersAwaitingBlock = new(new EqualityComparerByteArray());

    const int HEIGHT_GAP_MAX_BlockMissing = 20;
    internal Dictionary<int, Block> Blocks = new();


    internal Chain(Header headerGenesis, bool isRoot)
      : this(null, headerGenesis, isRoot)
    { }

    Chain(Chain chainParent, Header headerRoot, bool isRoot)
    {
      ChainParent = chainParent;
      HeaderRoot = headerRoot;
      HeaderTip = headerRoot;

      if (isRoot)
        HeaderTipBlockchain = headerRoot;
    }

    internal void InsertHeadersInTree(List<Header> headers, int heightBlockStoredOldest, out Header headerLastInTree)
    {
      Chain chain = this;
      headerLastInTree = null;

      if (!TrySearchHeaderAncestor(headers, ref chain, heightBlockStoredOldest, out Header headerAncestor))
        return;

      if (headers.Count == 0)
      {
        headerLastInTree = headerAncestor;
        return;
      }

      int indexAppendNext = 0;

      if (headerAncestor != chain.HeaderTip)
      {
        headers[0].AppendToHeader(headerAncestor);

        Chain chainChild = new(chain, headers[0], isRoot: false);
        chain.ChainsChild.Add(chainChild);
        chain = chainChild;

        indexAppendNext = 1;
      }

      for (int i = indexAppendNext; i < headers.Count; i++)
        chain.AppendHeader(headers[i]);

      headerLastInTree = headers[^1];
    }

    static bool TrySearchHeaderAncestor(
      List<Header> headers,
      ref Chain chain,
      int heightBlockStoredOldest,
      out Header headerAncestor)
    {
      headerAncestor = chain.HeaderTip;

      while (!headerAncestor.Hash.IsAllBytesEqual(headers[0].HashPrevious))
      {
        if (headerAncestor == chain.HeaderRoot || headerAncestor.Height <= heightBlockStoredOldest)
        {
          foreach (Chain chainChild in chain.ChainsChild)
          {
            chain = chainChild;

            if (TrySearchHeaderAncestor(headers, ref chain, heightBlockStoredOldest, out headerAncestor))
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

    internal T FindInTree<T>(Func<Chain, T> search) where T : class
    {
      if (search(this) is T result)
        return result;

      foreach (Chain chainChild in ChainsChild)
        if (chainChild.FindInTree(search) is T resultChild)
          return resultChild;

      return null;
    }

    internal bool TryGetHeaderPlaceholder(Header headerParent, out Header headerPlaceholder, out Chain chainPlaceholder)
    {
      Header headerFound = null;

      chainPlaceholder = FindInTree(chain =>
      {
        headerFound = chain.FindHeaderAnchoredIn(headerParent);
        return headerFound != null ? chain : null;
      });

      headerPlaceholder = headerFound;
      return chainPlaceholder != null;
    }

    Header FindHeaderAnchoredIn(Header headerParent)
    {
      Header headerFork = HeaderRoot.HeaderPrevious;
      Header header = HeaderTip;

      while (header != headerFork && header.HeaderParent != headerParent)
        header = header.HeaderPrevious;

      if (header == headerFork)
        return null;

      return header;
    }

    internal void CutOff(Header headerRemove)
    {
      Header headerPrevious = headerRemove.HeaderPrevious;

      ChainsChild.RemoveAll(c => c.HeaderRoot.Height > headerRemove.Height);

      RemoveBlocksAndHeadersAwaitingBlock(h => h >= headerRemove.Height);

      if (HeaderTipBlockchain != null && HeaderTipBlockchain.Height >= headerRemove.Height)
        HeaderTipBlockchain = headerPrevious;

      if (headerRemove == HeaderRoot)
      {
        ChainParent.ChainsChild.Remove(this);
        return;
      }

      headerPrevious.HeaderNext = null;
      HeaderTip = headerPrevious;

      Chain chainForkAtTip = ChainsChild
        .Where(c => c.HeaderRoot.HeaderPrevious == HeaderTip)
        .MaxBy(c => c.HeaderTipBlockchain != null ? c.HeaderTipBlockchain.Height : HeaderTip.Height);

      if (chainForkAtTip == null)
        return;

      ChainsChild.Remove(chainForkAtTip);

      HeaderTip.HeaderNext = chainForkAtTip.HeaderRoot;
      HeaderTip = chainForkAtTip.HeaderTip;

      chainForkAtTip.MoveBlockDownloadsAndForksTo(this, int.MaxValue);

      if (ChainParent != null)
        AdvanceTipChain();
    }

    internal void ReRoot(Header headerRoot)
    {
      DropForksDeep(headerRoot.Height);
      RemoveBlocksAndHeadersAwaitingBlock(h => h <= headerRoot.Height);

      headerRoot.HeaderPrevious = null;
      HeaderRoot = headerRoot;
      HeaderTipBlockchain = headerRoot;
    }

    void RemoveBlocksAndHeadersAwaitingBlock(Func<int, bool> isHeightRemoved)
    {
      foreach (int height in Blocks.Keys.Where(isHeightRemoved).ToList())
        Blocks.Remove(height);

      foreach (Header header in HeadersAwaitingBlock.Values.Where(h => isHeightRemoved(h.Height)).ToList())
        HeadersAwaitingBlock.Remove(header.Hash);
    }

    internal void DropForksDeep(int heightBlockStoredOldest)
    {
      foreach (Chain chainChild in ChainsChild.Where(c => c.HeaderRoot.HeaderPrevious.Height < heightBlockStoredOldest).ToList())
        chainChild.CutOff(chainChild.HeaderRoot);
    }

    internal void AppendHeader(Header header)
    {
      header.AppendToHeader(HeaderTip);

      HeaderTip.HeaderNext = header;
      HeaderTip = header;
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
        && headerCandidate.Height <= heightBlockNext + HEIGHT_GAP_MAX_BlockMissing)
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

      MoveBlockDownloadsAndForksTo(chainParent, heightFork);

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

    void MoveBlockDownloadsAndForksTo(Chain chainTarget, int heightMax)
    {
      MoveItems(Blocks, chainTarget.Blocks, b => b.Key <= heightMax);
      MoveItems(HeadersAwaitingBlock, chainTarget.HeadersAwaitingBlock, h => h.Value.Height <= heightMax);
      MoveItems(ChainsChild, chainTarget.ChainsChild, c => c.HeaderRoot.HeaderPrevious.Height <= heightMax);

      foreach (Chain chainChild in chainTarget.ChainsChild)
        chainChild.ChainParent = chainTarget;
    }

    static void MoveItems<T>(ICollection<T> source, ICollection<T> target, Func<T, bool> predicate)
    {
      foreach (T item in source.Where(predicate).ToList())
      {
        source.Remove(item);
        target.Add(item);
      }
    }

    internal bool IsStrongerThan(Chain chain)
    {
      return HeaderTipBlockchain != null
        && HeaderTipBlockchain.Height > chain.HeaderTipBlockchain.Height;
    }

    internal Chain FindForkStrongest()
    {
      Chain chainStrongest = null;

      foreach (Chain chainChild in ChainsChild)
      {
        bool isConnected = chainChild.HeaderTipBlockchain != null
          && chainChild.HeaderRoot.HeaderPrevious.Height <= HeaderTipBlockchain.Height;

        if (!isConnected)
          continue;

        Chain chainStrongestBelowChild = chainChild.FindForkStrongest();

        Chain chainStrongestOfChild = chainStrongestBelowChild != null
          && chainStrongestBelowChild.IsStrongerThan(chainChild)
          ? chainStrongestBelowChild : chainChild;

        if (chainStrongest == null || chainStrongestOfChild.IsStrongerThan(chainStrongest))
          chainStrongest = chainStrongestOfChild;
      }

      return chainStrongest;
    }
  }
}
