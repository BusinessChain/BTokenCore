using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;


namespace BTokenCore;

internal partial class Network
{
  SemaphoreSlim SemaphoreBlockchainRoot = new(1);
  internal Blockchain BlockchainRoot;

  string PathBlocksMined = "blocksMined";
  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();

  internal async Task<bool> TryLockBlockchain(int timeout)
  {
    if (NetworkParent != null)
      return await NetworkParent.TryLockBlockchain(timeout);

    return await SemaphoreBlockchainRoot.WaitAsync(timeout).ConfigureAwait(false);
  }

  void ReleaseLockBlockchain()
  {
    if (NetworkParent != null)
      NetworkParent.ReleaseLockBlockchain();
    else
      SemaphoreBlockchainRoot.Release();
  }

  internal async Task<List<byte[]>> ExtendHeaderchain(
    Header headerRoot,
    Block blockDownload)
  {
    List<byte[]> headerslocator = null;

    if (!await TryLockBlockchain(10000))
      return headerslocator;

    try
    {
      BlockchainRoot.TryExtendHeaderchain(
        headerRoot,
        out headerslocator,
        blockDownload);

      return headerslocator;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal List<Block> PoolBlocks = new();

  internal async Task<Block> InsertBlock(Block block)
  {
    if (await TryLockBlockchain(10000))
      try
      {
        Blockchain chain = BlockchainRoot.FindChain(block);

        if (chain == null)
          return block;

        do
        {
          if (chain == BlockchainRoot)
          {
            Token.InsertBlock(block);
            NotifyChildNetworksOfAnchorToken(block);
          }

          chain.HeaderTipBlockchain = block.Header;

          block.WriteToDisk(chain.PathDirectoryBlocks);

          PoolBlocks.Add(block);

        } while (chain.QueueBlocks.TryGetValue(chain.HeaderTipBlockchain.Height + 1, out block));

        if (chain.IsHigherThan(BlockchainRoot))
          Reorg(chain);

        block = TakeFromBlockPool();

        block.Header = chain.FetchHeaderDownload();
      }
      finally
      {
        ReleaseLockBlockchain();
      }

    return block;
  }

  void Reorg(Blockchain chain)
  {

  }

  Block TakeFromBlockPool()
  {
    if (!PoolBlocks.Any())
      return new(Token);

    Block block = PoolBlocks[0];
    PoolBlocks.RemoveAt(0);

    return block;
  }

  bool TryGetBlockMined(out Block block, byte[] hash)
  {
    block = BlocksMinedCache
      .Find(b => b.Header.Hash.IsAllBytesEqual(hash));

    if (block == null)
    {
      string pathFileBlock = Path.Combine(PathBlocksMined, block.Header.Hash.ToHexString());

      if (!File.Exists(pathFileBlock))
        return false;

      block = new(Token, File.ReadAllBytes(pathFileBlock));
      block.Parse();
    }

    return block.Header.HashPrevious.IsAllBytesEqual(BlockchainRoot.HeaderTipBlockchain.Hash);
  }

  void OnTokenAnchorParent(TXOutputTokenAnchor tokenAnchor)
  {
    try
    {
      if (!TryGetBlockMined(out Block block, tokenAnchor.HashBlockReferenced))
        return;

      BlockchainRoot.AppendHeader(block.Header);

      Token.InsertBlock(block);
      NotifyChildNetworksOfAnchorToken(block);

      BlockchainRoot.HeaderTipBlockchain = block.Header;

      block.WriteToDisk(BlockchainRoot.PathDirectoryBlocks);

      PoolBlocks.Add(block);

      // Hier ein sendBlock machen und intern zuerst header und dann wenn
      // getdata kommt blcok aus peer cache laden, statt wieder node anfragen.
      lock (LOCK_Peers)
        Peers.ForEach(p => HeadersMessage.SendHeaders(
          p,
          new List<byte[]> { block.Header.Hash }));

      // Der User muss jeweils definieren, mit welcher fee Rate er die Verankerung bezahlen will.
      // Dem user kann im GUI auch ein Tool zur verfügung gestellt werden welches ihm 
      // erlaubt, die Fee Rate automatisiert zu steuern. z.B. anhand vergangener Fee Raten
      // oder Marktpreis Arbitrierung.

      if (IsMining)
      {
        block = BlockchainRoot.MineBlock(out TXOutputTokenAnchor anchorToken);

        BlocksMinedCache.Add(block);

        block.WriteToDisk(PathBlocksMined);

        NetworkParent.MineTokenAnchor(tokenAnchor);
      }
    }
    catch (Exception ex)
    {
      return;
    }
  }

  void MineTokenAnchor(TXOutputTokenAnchor tokenAnchor)
  {
    if (Token.TryCreateTXAnchor(tokenAnchor, FeePerByte, out TX tX))
      lock (LOCK_Peers)
        foreach (Peer peer in Peers)
          peer.BroadcastTX(tX);
    else
    {
      IsMining = false;
    }
  }

  List<byte[]> GetLocator()
  {
    lock (BlockchainRoot)
      return BlockchainRoot.GetLocator();
  }

  internal async Task<(List<byte[]> headers, int heightAncestor)> GetHeadersSerialized(
    List<byte[]> hashesLocator,
    int maxCountHeaders)
  {
    if (!await TryLockBlockchain(10000))
      return (headers: new(), heightAncestor: -1);

    try
    {
      return BlockchainRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task GetBlock(byte[] hash, Block blockUpload)
  {
    if (!await TryLockBlockchain(10000))
      return;

    try
    {
      BlockchainRoot.GetBlock(hash, blockUpload);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}