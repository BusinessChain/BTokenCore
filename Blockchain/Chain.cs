namespace BTokenCore;

internal partial class Blockchain
{
  class Chain
  {
    internal Header HeaderTip;
    internal Header HeaderRoot;
    internal Header HeaderTipBlockchain;

    Chain ChainParent;
    List<Chain> ChainsChild = new();

    Dictionary<byte[], Header> HeadersAwaitingBlock = new(new EqualityComparerByteArray());
    Header HeaderDownloadNext;

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

      HeaderDownloadNext = headerRoot;
    }

    internal Header TryExtendHeaderchain(List<Header> headers)
    {
      Chain chain = this;

      if (!TrySearchHeaderAncestor(headers, ref chain, out Header headerAncestor))
        return null;

      if (headers.Count == 0)
        return headerAncestor;

      if (headerAncestor != chain.HeaderTip)
      {
        headers[0].AppendToHeader(headerAncestor);

        Chain chainChild = new(chain, headers[0], isRoot: false);
        chain.ChainsChild.Add(chainChild);
        chain = chainChild;
      }
      else
        chain.AppendHeader(headers[0]);

      for (int i = 1; i < headers.Count; i++)
        chain.AppendHeader(headers[i]);

      return headers[^1];
    }

    internal static bool TrySearchHeaderAncestor(
      List<Header> headers,
      ref Chain chain,
      out Header headerAncestor)
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

    internal Chain FindChain(Header header)
    {
      if (HeaderRoot.Height <= header.Height && header.Height <= HeaderTip.Height)
      {
        Header headerInChain = HeaderTip;

        while (headerInChain.Height > header.Height)
          headerInChain = headerInChain.HeaderPrevious;

        if (headerInChain == header)
          return this;
      }

      foreach (Chain chainChild in ChainsChild)
        if (chainChild.FindChain(header) is Chain chain)
          return chain;

      return null;
    }

    internal Header FetchHeaderDownload(int heightMax)
    {
      if (ChainParent != null
        && (ChainParent.HeaderTipBlockchain == null
          || ChainParent.HeaderTipBlockchain.Height < HeaderRoot.Height - 1))
        return ChainParent.FetchHeaderDownload(HeaderRoot.Height - 1);

      int heightBlockNext = HeaderTipBlockchain != null
        ? HeaderTipBlockchain.Height + 1 : HeaderRoot.Height;

      if (HeaderDownloadNext == null
        || HeaderDownloadNext.Height > heightMax
        || HeaderDownloadNext.Height - heightBlockNext > DEPTH_MAX_BlockMissing)
        return HeadersAwaitingBlock.Values
          .Where(h => h.Height <= heightMax)
          .MinBy(h => h.Height);

      Header headerDownload = HeaderDownloadNext;
      HeadersAwaitingBlock.Add(headerDownload.Hash, headerDownload);
      HeaderDownloadNext = headerDownload.HeaderNext;
      return headerDownload;
    }

    internal bool TryQueueBlock(Block block, out Chain chainQueued)
    {
      if (HeadersAwaitingBlock.Remove(block.Header.Hash))
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

    internal void SwitchWithRootChain(Chain chainRootOld)
    {
      HeaderRoot = chainRootOld.HeaderRoot;
      chainRootOld.HeaderRoot = chainRootOld.HeaderTipBlockchain.HeaderNext;

      ChainsChild.Add(chainRootOld);
      ChainParent.ChainsChild.Remove(this);

      chainRootOld.ChainParent = this;
      ChainParent = null;
    }

    internal bool IsStrongerThan(Chain blockchain)
    {
      return HeaderTipBlockchain != null
        && HeaderTipBlockchain.Height > blockchain.HeaderTipBlockchain.Height;
    }
  }
}
