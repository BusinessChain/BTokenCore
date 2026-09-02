using LiteDB;


namespace BTokenCore;

internal class Blockchain
{
  Blockchain BlockchainParent;
  List<Blockchain> BlockchainBranches = new();

  internal Header HeaderTip;
  internal Header HeaderRoot;
  internal Header HeaderTipBlockchain;

  Dictionary<byte[], Header> HeadersInThisChain = new(new EqualityComparerByteArray());
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
    if (!TryFindHeaderchain(ref headerRoot, out Blockchain chain, out Header headerAncestor))
      return chain;

    if(chain.HeaderTip != headerAncestor)
    {
      foreach (Blockchain branch in BlockchainBranches)
        if (branch.HeaderRoot.Hash.IsAllBytesEqual(headerRoot.Hash))
          return branch.TryExtendHeaderchain(headerRoot.HeaderNext, out chain);

      Header headerTip = headerRoot.AppendToHeader(headerAncestor);
      chain.BlockchainBranches.Add(new(this, headerRoot, headerTip));
      return true;
    }

    chain.AppendHeader(headerRoot);
    return true;
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
        && HeadersInThisChain.Any())
      return HeadersInThisChain.Values.MinBy(h => h.Height);

    if (HeaderDownloadNext != null)
    {
      Header headerDownload = HeaderDownloadNext;
      HeadersInThisChain.Add(headerDownload.Hash, headerDownload);
      HeaderDownloadNext = HeaderDownloadNext.HeaderNext;
      return headerDownload;
    }

    return null;
  }

  /// <summary>
  /// Searches the chain that contains a maching header and queue the block.
  /// </summary>
  /// <param name="block"></param>
  /// <returns>The chain that now contains that block in its queue.</returns>
  /// <exception cref="ProtocolException"></exception>
  internal Blockchain InsertBlockInChain(Block block)
  {    
    if (HeadersInThisChain.Remove(block.Header.Hash))
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

  internal bool TryGetBlockNextFromQueue(out Block block)
  {
    return QueueBlocks.TryGetValue(HeaderTipBlockchain.Height + 1, out block);
  }

  internal void SwitchWithRootBranch(Header headerAncestor)
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

  internal bool IsStrongerThan(Blockchain blockchain)
  {
    return HeaderTipBlockchain.DifficultyAccumulated >
      blockchain.HeaderTipBlockchain.DifficultyAccumulated;
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
