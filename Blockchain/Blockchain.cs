using LiteDB;


namespace BTokenCore;

internal abstract partial class Blockchain
{
  internal const int MAX_COUNT_BLOCKS_STORED = 200;

  internal Wallet Wallet;

  internal int SizeBlockMax;

  protected Chain ChainRoot;

  protected LiteDatabase Database;
  protected ILiteCollection<BsonDocument> DatabaseBlockCollection;

  SemaphoreSlim SemaphoreBlockchain;

  internal event Action<Block> OnBlockInserted;
  internal event Action<Block> OnBlockRolledBack;

  Header HeaderTipBlockchainAnnounced;


  protected Blockchain(
    string nameToken,
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
  {
    Directory.CreateDirectory(nameToken);

    Wallet = new Wallet(File.ReadAllText($"Wallet{nameToken}/wallet"));

    SemaphoreBlockchain = semaphoreBlockchain;

    ChainRoot = new(CreateHeaderGenesis(), isRoot: true);
    ChainRoot.HeaderRoot.HeaderParent = blockchainParent?.ChainRoot.HeaderRoot;

    Database = new ($"Filename={nameToken + "Network"}.db;Mode=Exclusive");
    DatabaseBlockCollection = Database.GetCollection<BsonDocument>("blocks");

    AppDomain.CurrentDomain.ProcessExit += (s, e) => { Database.Dispose(); };
  }

  internal async Task<T> RunLocked<T>(Func<T> function)
  {
    await SemaphoreBlockchain.WaitAsync().ConfigureAwait(false);

    try
    {
      return function();
    }
    finally
    {
      SemaphoreBlockchain.Release();
    }
  }

  internal async Task RunLocked(Action action)
  {
    await RunLocked(() =>
    {
      action();
      return true;
    });
  }

  protected abstract Header CreateHeaderGenesis();

  internal abstract Header ParseHeader(byte[] buffer, ref int index);

  internal abstract TX ParseTX(byte[] buffer, ref int index, bool flagIsCoinbase = false);

  internal abstract TX GetTX(byte[] hash);

  protected void ParseBlockHeader(Block block, ref int startIndex)
  {
    Header header = ParseHeader(block.Buffer, ref startIndex);

    if (block.Header == null)
      block.Header = header;
    else if (block.Header.Hash.IsAllBytesEqual(header.Hash) && block.Header.HashPrevious.IsAllBytesEqual(header.HashPrevious))
      header.CopyTo(block.Header);
    else
      throw new ProtocolException($"Received unexpected block {header} expected was {block.Header}.");
  }

  void ParseBlock(Block block)
  {
    block.TXs.Clear();
    int startIndex = 0;

    ParseBlockHeader(block, ref startIndex);

    block.Header.CountTXs = VarInt.GetInt(block.Buffer, ref startIndex);

    if (block.Header.CountTXs == 0)
      throw new ProtocolException($"Block {block} lacks coinbase transaction.");

    HashSet<byte[]> hashesTX = new(new EqualityComparerByteArray());

    for (int t = 0; t < block.Header.CountTXs; t += 1)
    {
      TX tX = ParseTX(block.Buffer, ref startIndex, flagIsCoinbase: t == 0);

      if (!hashesTX.Add(tX.Hash))
        throw new ProtocolException($"Block {block.Header.Hash.ToHexString()} contains transaction {tX.Hash.ToHexString()} twice.");

      block.TXs.Add(tX);
    }

    if (!block.Header.MerkleRoot.IsAllBytesEqual(block.ComputeMerkleRoot()))
      throw new ProtocolException("Header merkle root not equal to computed transactions merkle root.");

    block.Header.Fee = block.TXs.Sum(t => t.Fee);

    block.Header.VerifyCoinbase(block.TXs[0].GetValueOutputs());

    foreach (TX tX in block.TXs)
      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput is TXOutputTokenAnchor tokenAnchor)
          block.Header.AnchorsWinner.TryAdd(tokenAnchor.IDToken, tokenAnchor);
  }

  protected virtual void ParseBlockStored(Block block)
  {
    ParseBlock(block);
  }

  protected abstract byte[] SerializeBlockStored(Block block);

  protected Block LoadBlock(int height)
  {
    if (DatabaseBlockCollection.FindById(height) is BsonDocument bsonDocumentBlock)
      return new Block(bsonDocumentBlock["blockBytes"].AsBinary);

    return null;
  }

  protected bool TryParseBlockStored(Header header, Block block)
  {
    block.Header = header;

    try
    {
      ParseBlockStored(block);
    }
    catch (ProtocolException)
    {
      block.Header = null;
      return false;
    }

    return true;
  }

  internal void GetBlock(byte[] hash, Block blockLoad)
  {
    if (ChainRoot.TryGetHeader(hash, out Header header)
      && LoadBlock(header.Height) is Block block)
    {
      blockLoad.LoadBuffer(block.Buffer);
      TryParseBlockStored(header, blockLoad);
    }
  }

  internal void LoadBlockchain()
  {
    foreach (BsonDocument bsonDocumentBlock in DatabaseBlockCollection.Find(Query.All(Query.Ascending)))
      try
      {
        Block blockLoad = new(bsonDocumentBlock["blockBytes"].AsBinary);
        ParseBlockStored(blockLoad);

        Header header = blockLoad.Header;

        if (header.HashPrevious.IsAllBytesEqual(ChainRoot.HeaderTipBlockchain.Hash))
          ChainRoot.TryAppendHeader(header);
        else
        {
          header.Height = bsonDocumentBlock["_id"].AsInt32;
          ChainRoot = new(header, isRoot: true);
        }

        ChainRoot.HeaderTipBlockchain = header;

        OnBlockInserted.Announce(blockLoad);
      }
      catch
      {
        continue;
      }
  }

  protected int GetHeightBlockStoredOldest()
  {
    BsonDocument bsonDocumentBlockOldest = DatabaseBlockCollection.Query().OrderBy("_id").Select("_id").FirstOrDefault();

    if (bsonDocumentBlockOldest == null)
      return ChainRoot.HeaderRoot.Height;

    return bsonDocumentBlockOldest["_id"].AsInt32;
  }

  protected abstract void InsertBlock(Block block);

  protected virtual void RollBackBlock(Block block) { }

  protected virtual void OnTipChanged() { }

  internal void ReceiveBlock(Block blockReceived)
  {
    Header headerAwaited = null;

    Chain chainAwaiting = ChainRoot.FindInTree(chain =>
      chain.HeadersAwaitingBlock.Remove(blockReceived.Header.Hash, out headerAwaited) ? chain : null);

    if (chainAwaiting == null)
      return;

    Block block = new(blockReceived.Buffer[..blockReceived.LengthDataPayload])
    {
      Header = headerAwaited
    };

    ParseBlock(block);
    chainAwaiting.Blocks.Add(headerAwaited.Height, block);

    UpdateTipBlockchain(chainAwaiting);
  }

  protected void UpdateTipBlockchain(Chain chain)
  {
    if (chain != ChainRoot)
      chain.AdvanceTipChain();

    do
    {
      InsertBlocksQueued();
      ChainRoot.DropForksDeep(GetHeightBlockStoredOldest());
    }
    while (TryReorgToForkStrongest());

    if (ChainRoot.HeaderTipBlockchain != HeaderTipBlockchainAnnounced)
    {
      HeaderTipBlockchainAnnounced = ChainRoot.HeaderTipBlockchain;
      OnTipChanged();
    }
  }

  protected void RunInTransaction(Action action)
  {
    Database.BeginTrans();

    try
    {
      action();
    }
    catch
    {
      Database.Rollback();
      throw;
    }

    Database.Commit();
  }

  void InsertBlocksQueued()
  {
    while (ChainRoot.Blocks.Remove(ChainRoot.HeaderTipBlockchain.Height + 1, out Block block))
    {
      try
      {
        RunInTransaction(() =>
        {
          InsertBlock(block);

          ChainRoot.HeaderTipBlockchain = block.Header;

          DatabaseBlockCollection.Upsert(new BsonDocument
          {
            ["_id"] = block.Header.Height,
            ["blockBytes"] = SerializeBlockStored(block)
          });
        });
      }
      catch (ProtocolException)
      {
        ChainRoot.CutOff(block.Header);
        continue;
      }

      OnBlockInserted.Announce(block);
    }
  }

  bool TryReorgToForkStrongest()
  {
    if (ChainRoot.FindForkStrongest() is not Chain chainForkStrongest
      || !chainForkStrongest.IsStrongerThan(ChainRoot))
      return false;

    Reorg(chainForkStrongest);
    return true;
  }

  void Reorg(Chain chain)
  {
    while (chain != ChainRoot)
    {
      if(chain.ChainParent == ChainRoot)
        RollBack(heightAfterRollBack: chain.HeaderRoot.Height - 1);

      chain.SwitchWithParent();
      chain = chain.ChainParent;
    }
  }

  protected void RollBack(int heightAfterRollBack)
  {
    while (ChainRoot.HeaderTipBlockchain.Height > heightAfterRollBack)
    {
      Header header = ChainRoot.HeaderTipBlockchain;
      if (LoadBlock(header.Height) is not Block block || !TryParseBlockStored(header, block))
        throw new InvalidOperationException($"Stored block at height {header.Height} cannot be loaded for rollback.");

      RunInTransaction(() =>
      {
        RollBackBlock(block);

        DatabaseBlockCollection.Delete(header.Height);
      });

      OnBlockRolledBack.Announce(block);

      ChainRoot.HeaderTipBlockchain = header.HeaderPrevious;

      ChainRoot.Blocks.Add(header.Height, block);
    }
  }

  internal int GetHeight()
  {
    return ChainRoot.HeaderTipBlockchain.Height;
  }

  internal Header TryInsertHeadersInTree(List<Header> headers)
  {
    ChainRoot.InsertHeadersInTree(headers, GetHeightBlockStoredOldest(), out Header headerLastInTree);

    return headerLastInTree;
  }

  internal Header FetchHeaderBlockMissingNext(Func<Header, bool> canSupplierDeliverBlock)
  {
    return ChainRoot.FindInTree(chain =>
      chain.HeaderTip.Height > ChainRoot.HeaderTipBlockchain.Height
        ? chain.FetchHeaderBlockMissingNextInChain(chain.HeaderTip.Height, canSupplierDeliverBlock)
        : null);
  }

  internal List<byte[]> GetLocator()
  {
    return ChainRoot.GetLocator();
  }

  internal (List<byte[]> headers, int heightAncestor) GetHeadersSerialized(
    List<byte[]> hashesLocator,
    int maxCountHeaders)
  {
    return ChainRoot.GetHeadersSerialized(hashesLocator, maxCountHeaders);
  }
}
