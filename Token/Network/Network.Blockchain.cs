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

  /// <summary>
  /// Returns block because ref block is not possible with async.
  /// </summary>
  internal async Task<Block> InsertBlockReturnNewBlock(Block block)
  {
    try
    {
      await LockBlockchain();

      InsertBlock(block);
    }
    finally
    {
      ReleaseLockBlockchain();
    }

    return block; // because ref block is not possible with async.
  }

  internal void InsertBlock(Block block)
  {
    Blockchain chain = BlockchainRoot.InsertBlockInChain(block);

    while (chain.TryGetBlockNextFromQueue(out block))
    {
      if (chain == BlockchainRoot)
        WriteBlock(block);
      else if (chain.IsStrongerThan(BlockchainRoot))
      {
        while (BlockchainRoot.HeaderTipBlockchain.Height > chain.HeaderRoot.Height - 1)
        {
          Block blockRollback = BlockchainRoot.Rollback();
          Token.ReverseBlock(blockRollback);

          NotifyChildNetworksOfRollback(blockRollback);
        }

        chain.BlockchainBranches.Add(BlockchainRoot);
        BlockchainRoot = chain;
        chain.SwitchWithRootBranch();
      }

      Token.ReturnBlock(block);
    }

    block = Token.GetBlock();

    block.Header = chain.FetchHeaderDownload();
  }

  void WriteBlock(Block block)
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

    NotifyChildNetworksIfAnchorToken(block);
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

        InsertBlock(block);
      }

      // Der User muss jeweils definieren, mit welcher fee Rate er die Verankerung bezahlen will.
      // Dem user kann im GUI auch ein Tool zur verfügung gestellt werden welches ihm 
      // erlaubt, die Fee Rate automatisiert zu steuern. z.B. anhand vergangener Fee Raten
      // oder Marktpreis Arbitrierung.

      if (IsMining)
      {
        Token.MineBlock(
          BlockchainRoot.HeaderTip.Height + 1,
          block,
          out TXOutputTokenAnchor anchorToken);

        block.Header.HashPrevious = BlockchainRoot.HeaderTip.Hash;

        block.Header.ComputeHash();

        block.Serialize();

        BlocksMinedCache.Add(block);

        block.WriteToDisk(PathBlocksMined); // write to LiteDB

        NetworkParent.MineTokenAnchor(anchorToken);
      }
    }
    catch (Exception ex)
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