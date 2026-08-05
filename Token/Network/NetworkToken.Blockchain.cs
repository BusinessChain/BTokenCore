using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;


namespace BTokenCore;

public partial class NetworkToken
{
  SemaphoreSlim SemaphoreBlockchainRoot = new(1);
  Blockchain BlockchainRoot;

  ConcurrentBag<Block> PoolBlocks = new();

  string PathBlocksMined = "blocksMined";
  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();

  async Task<bool> TryLockBlockchain(int timeout)
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

  public async Task<List<byte[]>> ExtendHeaderchain(
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

  async Task<Block> InsertBlock(Block block)
  {
    if (await TryLockBlockchain(10000))
      try
      {
        BlockchainRoot.TryAppendBlock(ref block, ref BlockchainRoot);
      }
      finally
      {
        ReleaseLockBlockchain();
      }

    return block;
  }

  void OnTokenAnchorParent(TXOutputTokenAnchor tokenAnchor)
  {
    try
    {
      Block blockMined = BlocksMinedCache
        .Find(b => b.Header.Hash.IsAllBytesEqual(tokenAnchor.HashBlockReferenced));

      if (blockMined == null)
      {
        string pathFileBlock = Path.Combine(PathBlocksMined, blockMined.Header.Height.ToString());

        if (!File.Exists(pathFileBlock))
          return;

        blockMined = new(Token, File.ReadAllBytes(pathFileBlock));
        blockMined.Parse();
      }

      if (BlockchainRoot.TryExtendHeaderchain(blockMined.Header, out List<byte[]> headerslocator, blockMined))
        if (BlockchainRoot.TryAppendBlock(ref blockMined, ref BlockchainRoot))
        {
          // Hier ein sendBlock machen und intern zuerst header und dann wenn
          // getdata kommt blcok aus peer cache laden, statt wieder node anfragen.
          lock (LOCK_Peers)
            Peers.ForEach(p => HeadersMessage.SendHeaders(
              p,
              new List<byte[]> { blockMined.Header.Hash }));
        }

      // Der User muss jeweils definieren, mit welcher fee Rate er die Verankerung bezahlen will.
      // Dem user kann im GUI auch ein Tool zur verfügung gestellt werden welches ihm 
      // erlaubt, die Fee Rate automatisiert zu steuern. z.B. anhand vergangener Fee Raten
      // oder Marktpreis Arbitrierung.

      if (IsMining)
      {
        Block block = BlockchainRoot.MineBlock(out TXOutputTokenAnchor anchorToken);

        BlocksMinedCache.Add(block);

        block.WriteToDisk(PathBlocksMined);

        NetworkParent.MineTokenAnchor(tokenAnchor);
      }
    }
    catch (Exception ex)
    {
      SocketToken.Log($"{ex.GetType().Name} when attempting to load mined block {tokenAnchor.HashBlockReferenced.ToHexString()}: {ex.Message}.\n");
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
      $"Could not create anchor tX, stop mining.".Log(this, SocketToken);
      IsMining = false;
    }
  }

  List<byte[]> GetLocator()
  {
    lock (BlockchainRoot)
      return BlockchainRoot.GetLocator();
  }

  async Task<(List<byte[]> headers, int heightAncestor)> GetHeadersSerialized(
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

  public async Task GetBlock(byte[] hash, Block blockUpload)
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

  public void StartMining()
  {
    if (NetworkParent == null)
      return;

    IsMining = true;
  }
}