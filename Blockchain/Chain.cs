namespace BTokenCore;

internal partial class Blockchain
{
  protected class Chain
  {
    internal Header HeaderTip;
    internal Header HeaderRoot;
    internal Header HeaderTipBlockchain;

    internal Chain ChainTrunk;
    List<Chain> ChainsFork = new();

    internal Dictionary<byte[], Header> HeadersAwaitingBlock = new(new EqualityComparerByteArray());

    const int HEIGHT_GAP_MAX_BLOCK_MISSING = 20;
    internal Dictionary<int, Block> Blocks = new();


    internal Chain(Header headerGenesis, bool isRoot)
      : this(null, headerGenesis, isRoot)
    { }

    Chain(Chain chainTrunk, Header headerRoot, bool isRoot)
    {
      ChainTrunk = chainTrunk;
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

        Chain chainFork = new(chain, headers[0], isRoot: false);
        chain.ChainsFork.Add(chainFork);
        chain = chainFork;

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
          foreach (Chain chainFork in chain.ChainsFork)
          {
            chain = chainFork;

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
          foreach (Chain chainFork in chain.ChainsFork)
            if (chainFork.HeaderRoot.HeaderPrevious == headerAncestor
              && chainFork.HeaderRoot.Hash.IsAllBytesEqual(headers[0].Hash))
            {
              chain = chainFork;
              headerAncestor = chainFork.HeaderRoot;
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

      foreach (Chain chainFork in ChainsFork)
        if (chainFork.FindInTree(search) is T resultFork)
          return resultFork;

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

      ChainsFork.RemoveAll(c => c.HeaderRoot.Height > headerRemove.Height);

      RemoveBlocksAndHeadersAwaitingBlock(h => h >= headerRemove.Height);

      if (HeaderTipBlockchain != null && HeaderTipBlockchain.Height >= headerRemove.Height)
        HeaderTipBlockchain = headerPrevious;

      if (headerRemove == HeaderRoot)
      {
        ChainTrunk.ChainsFork.Remove(this);
        return;
      }

      headerPrevious.HeaderNext = null;
      HeaderTip = headerPrevious;

      Chain chainForkAtTip = ChainsFork
        .Where(c => c.HeaderRoot.HeaderPrevious == HeaderTip)
        .MaxBy(c => c.HeaderTipBlockchain != null ? c.HeaderTipBlockchain.Height : HeaderTip.Height);

      if (chainForkAtTip == null)
        return;

      ChainsFork.Remove(chainForkAtTip);

      HeaderTip.HeaderNext = chainForkAtTip.HeaderRoot;
      HeaderTip = chainForkAtTip.HeaderTip;

      chainForkAtTip.MoveBlockDownloadsAndForksTo(this, int.MaxValue);

      if (ChainTrunk != null)
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
      foreach (Chain chainFork in ChainsFork.Where(c => c.HeaderRoot.HeaderPrevious.Height < heightBlockStoredOldest).ToList())
        chainFork.CutOff(chainFork.HeaderRoot);
    }

    internal void AppendHeader(Header header)
    {
      header.AppendToHeader(HeaderTip);

      HeaderTip.HeaderNext = header;
      HeaderTip = header;
    }

    internal Header FetchHeaderBlockMissingNextInChain(int heightTarget, Func<Header, bool> canSupplierDeliverBlock)
    {
      bool isChainTrunkLagging = ChainTrunk != null
        && (ChainTrunk.HeaderTipBlockchain == null
          || ChainTrunk.HeaderTipBlockchain.Height < HeaderRoot.Height - 1);

      if (isChainTrunkLagging)
        return ChainTrunk.FetchHeaderBlockMissingNextInChain(HeaderRoot.Height - 1, canSupplierDeliverBlock);

      int heightBlockNext = HeaderTipBlockchain != null
        ? HeaderTipBlockchain.Height + 1 : HeaderRoot.Height;

      Header headerCandidate = heightBlockNext == HeaderRoot.Height
        ? HeaderRoot : HeaderTipBlockchain.HeaderNext;

      while (headerCandidate != null
        && headerCandidate.Height <= heightTarget
        && headerCandidate.Height <= heightBlockNext + HEIGHT_GAP_MAX_BLOCK_MISSING)
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

    internal void SwitchWithTrunk()
    {
      Chain chainTrunk = ChainTrunk;
      Header headerFork = HeaderRoot.HeaderPrevious;
      int heightFork = headerFork.Height;

      (Blocks, chainTrunk.Blocks) = (chainTrunk.Blocks, Blocks);
      (HeadersAwaitingBlock, chainTrunk.HeadersAwaitingBlock) = (chainTrunk.HeadersAwaitingBlock, HeadersAwaitingBlock);
      (ChainsFork, chainTrunk.ChainsFork) = (chainTrunk.ChainsFork, ChainsFork);

      MoveBlockDownloadsAndForksTo(chainTrunk, heightFork);

      foreach (Chain chainFork in ChainsFork)
        chainFork.ChainTrunk = this;

      Header headerRootFork = headerFork.HeaderNext;
      headerFork.HeaderNext = HeaderRoot;
      HeaderRoot = headerRootFork;
      (HeaderTip, chainTrunk.HeaderTip) = (chainTrunk.HeaderTip, HeaderTip);

      if (chainTrunk.ChainTrunk != null)
      {
        chainTrunk.HeaderTipBlockchain = null;
        chainTrunk.AdvanceTipChain();
      }

      HeaderTipBlockchain = null;
      AdvanceTipChain();
    }

    void MoveBlockDownloadsAndForksTo(Chain chainTarget, int heightMax)
    {
      MoveItems(Blocks, chainTarget.Blocks, b => b.Key <= heightMax);
      MoveItems(HeadersAwaitingBlock, chainTarget.HeadersAwaitingBlock, h => h.Value.Height <= heightMax);
      MoveItems(ChainsFork, chainTarget.ChainsFork, c => c.HeaderRoot.HeaderPrevious.Height <= heightMax);

      foreach (Chain chainFork in chainTarget.ChainsFork)
        chainFork.ChainTrunk = chainTarget;
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

      foreach (Chain chainFork in ChainsFork)
      {
        bool isConnected = chainFork.HeaderTipBlockchain != null
          && chainFork.HeaderRoot.HeaderPrevious.Height <= HeaderTipBlockchain.Height;

        if (!isConnected)
          continue;

        Chain chainStrongestBelowFork = chainFork.FindForkStrongest();

        Chain chainStrongestOfFork = chainStrongestBelowFork != null
          && chainStrongestBelowFork.IsStrongerThan(chainFork)
          ? chainStrongestBelowFork : chainFork;

        if (chainStrongest == null || chainStrongestOfFork.IsStrongerThan(chainStrongest))
          chainStrongest = chainStrongestOfFork;
      }

      return chainStrongest;
    }
  }
}
