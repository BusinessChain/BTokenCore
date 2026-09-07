using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class Network
{
  SemaphoreSlim SemaphoreBlockchainRoot = new(1);
  internal Blockchain BlockchainRoot;

  string PathBlocksMined = "blocksMined";
  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();


  internal async Task LockBlockchain()
  {
    if (NetworkParent != null)
      await NetworkParent.LockBlockchain();

    await SemaphoreBlockchainRoot.WaitAsync().ConfigureAwait(false);
  }

  internal void ReleaseLockBlockchain()
  {
    if (NetworkParent != null)
      NetworkParent.ReleaseLockBlockchain();
    else
      SemaphoreBlockchainRoot.Release();
  }

  void LoadBlockchain()
  {
    SHA256 sHA256 = SHA256.Create();
    Block blockLoad = new(Token);

    int height = 1;
    BsonDocument headerDB = DatabaseHeaderCollection.FindById(height);

    while (headerDB != null)
      try
      {
        byte[] headerBytes = headerDB["headerBytes"].AsBinary;
        int startIndex = 0;

        Header header = Token.ParseHeader(headerBytes, ref startIndex, sHA256);

        BlockchainRoot.AppendHeader(header);

        BsonDocument blockDB = DatabaseBlockCollection.FindById(height);
        if (blockDB != null)
        {
          blockLoad.Buffer = headerDB["blockBytes"].AsBinary;
          blockLoad.Header = header;
          blockLoad.Parse();

          Token.InsertBlock(blockLoad);
        }

        height++;
        headerDB = DatabaseHeaderCollection.FindById(height);
      }
      catch
      {
        break;
      }
  }


  internal async Task<Blockchain> TryExtendHeaderchain(Header headerRoot)
  {
    try
    {
      await LockBlockchain(); // evt. mit LOCK_Node arbeiten

      return BlockchainRoot.TryExtendHeaderchain(headerRoot);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<Block> InsertBlockReturnNextBlock(Block block)
  {
    try
    {
      await LockBlockchain();

      InsertBlock(ref block, out Blockchain chain);

      block = Token.GetBlock();
      block.Header = chain.FetchHeaderDownload();
      return block;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal void InsertBlock(ref Block block, out Blockchain chain)
  {
    chain = BlockchainRoot.InsertBlockInChain(block);

    while (chain.TryGetBlockNextFromQueue(out block))
    {
      if (chain == BlockchainRoot)
      {
        Token.InsertBlock(block);

        DatabaseHeaderCollection.Insert(new BsonDocument
        {
          ["_id"] = block.Header.Height,
          ["headerBytes"] = block.Header.Serialize()
        });
        DatabaseBlockCollection.Insert(new BsonDocument
        {
          ["_id"] = block.Header.Height,
          ["blockBytes"] = block.Buffer
        });

        NotifyChildNetworksOfAnchorTokens(
          block,
          (networkChild, tokenAnchor) => networkChild.OnTokenAnchorParent(tokenAnchor));
      }
      else if (chain.IsStrongerThan(BlockchainRoot))
      {
        while (BlockchainRoot.HeaderTipBlockchain.Height
          > chain.HeaderRoot.Height - 1)
        {
          Block blockRollback = BlockchainRoot.RollBack();

          Token.ReverseBlock(blockRollback);

          NotifyChildNetworksOfAnchorTokens(
            blockRollback,
            (network, tokenAnchor) => network.OnTokenAnchorParentRollback(tokenAnchor));
        }

        chain.BlockchainBranches.Add(BlockchainRoot);
        BlockchainRoot = chain;
        chain.SwitchWithRootBranch();// clean up queue, and DB of blocks
      }

      Token.ReturnBlock(block);
    }

    block = null;
  }

  /// <summary>
  /// Performs action(network, tokenAnchor) with the child networks who are referenced by anchor tokens.
  /// </summary>
  void NotifyChildNetworksOfAnchorTokens(
    Block block,
    Action<Network, TXOutputTokenAnchor> action)
  {
    Dictionary<byte[], TXOutputTokenAnchor> cacheAnchorTokens =
        new(new EqualityComparerByteArray());

    foreach (TX tX in block.TXs)
      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput is TXOutputTokenAnchor tokenAnchor &&
            cacheAnchorTokens.TryAdd(tokenAnchor.HashBlockReferenced, tokenAnchor))
          if (NetworksChild.Find(n => n.Token.IDToken.IsAllBytesEqual(tokenAnchor.IDToken)) is Network network)
            action(network, tokenAnchor);
  }

  
  void OnTokenAnchorParentRollback(TXOutputTokenAnchor tokenAnchor)
  {

  }

  void OnTokenAnchorParent(TXOutputTokenAnchor tokenAnchor)
  {
    try
    {
      if (TryGetBlockMined(out Block block, tokenAnchor.HashBlockReferenced))
      {
        BlockchainRoot.TryExtendHeaderchain(block.Header);

        // Hier ein sendBlock machen und intern zuerst header und dann wenn
        // getdata kommt blcok aus peer cache laden, statt wieder node anfragen.
        lock (LOCK_Peers)
          Peers.ForEach(p => HeadersMessage.SendHeaders(
            p,
            new List<byte[]> { block.Header.Hash }));

        InsertBlock(ref block, out Blockchain chain);
      }

      // Der User muss jeweils definieren, mit welcher fee Rate er die Verankerung bezahlen will.
      // Dem user kann im GUI auch ein Tool zur verfügung gestellt werden welches ihm 
      // erlaubt, die Fee Rate automatisiert zu steuern. z.B. anhand vergangener Fee Raten
      // oder Marktpreis Arbitrierung.

      if (IsMining)
      {
        block = Token.MineBlock(
          BlockchainRoot.HeaderTip.Height + 1,
          out TXOutputTokenAnchor anchorToken);

        block.Header.HashPrevious = BlockchainRoot.HeaderTip.Hash;

        block.Header.ComputeHash();

        block.Serialize();

        BlocksMinedCache.Add(block);

        block.WriteToDisk(PathBlocksMined); // write to LiteDB

        NetworkParent.MineTokenAnchor(anchorToken);
      }
    }
    catch
    {
      return;
    }
  }

  bool TryGetBlockMined(out Block block, byte[] hash)
  {
    block = BlocksMinedCache
      .Find(b => b.Header.Hash.IsAllBytesEqual(hash));

    if (block == null)
    {
      // get from LiteDB instead.
      string pathFileBlock = Path.Combine(PathBlocksMined, block.Header.Hash.ToHexString());

      if (!File.Exists(pathFileBlock))
        return false;

      block = new(Token, File.ReadAllBytes(pathFileBlock));
      block.Parse();
    }

    return true;
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
    try
    {
      await LockBlockchain();
      return BlockchainRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}