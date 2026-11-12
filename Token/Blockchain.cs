using LiteDB;


namespace BTokenCore;

internal abstract partial class Blockchain
{
  protected const string NAME_FIELD_ID = "_id";
  protected const string NAME_FIELD_BLOCK_BYTES = "blockBytes";
  protected const string NAME_FIELD_HEIGHT_CONFIRMED = "heightConfirmed";
  const string NAME_FIELD_TX_RAW = "tXRaw";
  const string NAME_FIELD_FEE = "fee";
  const int SECONDS_DELAY_REQUEST_HEDGED = 5;
  protected const int BITCOIN_REORG_MARGIN = 6;

  internal Wallet Wallet;

  internal readonly int SizeBlockMax;

  protected Chain ChainRoot;

  protected LiteDatabase LiteDatabase;
  protected ILiteCollection<BsonDocument> BlocksStored;
  protected ILiteCollection<BsonDocument> TXsOwnedStored;

  SemaphoreSlim SemaphoreBlockchain;

  internal event Action<Block> OnBlockInserted;
  internal event Action<Block> OnBlockRolledBack;


  protected Blockchain(
    string nameToken,
    int sizeBlockMax,
    Blockchain blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
  {
    Directory.CreateDirectory(nameToken);

    Wallet = new Wallet($"Wallet{nameToken}/wallet");

    SizeBlockMax = sizeBlockMax;

    SemaphoreBlockchain = semaphoreBlockchain;

    ChainRoot = new(CreateHeaderRoot(), isRoot: true);
    ChainRoot.HeaderRoot.HeaderParent = blockchainParent?.ChainRoot.HeaderRoot;

    LiteDatabase = new (
      $"Filename={nameToken + "Network"}.db;Mode=Exclusive",
      new BsonMapper { IncludeFields = true, IncludeNonPublic = true });
    BlocksStored = LiteDatabase.GetCollection<BsonDocument>("blocks");
    TXsOwnedStored = LiteDatabase.GetCollection<BsonDocument>("tXsOwned");

    AppDomain.CurrentDomain.ProcessExit += (s, e) => { LiteDatabase.Dispose(); };
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

  protected abstract Header CreateHeaderRoot();

  internal abstract Header ParseHeader(byte[] buffer, ref int index);

  internal abstract TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures);

  internal abstract bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor, out int lockTime);

  internal abstract int CountAnchorsMissed();

  internal virtual TX GetTX(byte[] hash)
  {
    if (TXsOwnedStored.FindById(hash) is BsonDocument documentTXOwned && documentTXOwned[NAME_FIELD_HEIGHT_CONFIRMED].AsInt32 == 0)
      return ParseTXOwned(documentTXOwned);

    return null;
  }

  protected TX ParseTXOwned(BsonDocument documentTXOwned)
  {
    byte[] tXRaw = documentTXOwned[NAME_FIELD_TX_RAW].AsBinary;
    int index = 0;

    TX tX = ParseTX(tXRaw, ref index, isCoinbase: false, verifySignatures: true);

    tX.TXRaw = tXRaw;
    tX.Fee = documentTXOwned[NAME_FIELD_FEE].AsInt64;

    return tX;
  }

  internal List<byte[]> GetHashesTXOwnedUnconfirmed()
  {
    return TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).Select(d => d[NAME_FIELD_ID].AsBinary).ToList();
  }

  protected void InsertTXOwned(TX tX)
  {
    TXsOwnedStored.Insert(new BsonDocument
    {
      [NAME_FIELD_ID] = tX.Hash,
      [NAME_FIELD_TX_RAW] = tX.TXRaw,
      [NAME_FIELD_FEE] = tX.Fee,
      [NAME_FIELD_HEIGHT_CONFIRMED] = 0
    });
  }

  protected abstract bool IsTXOwnedConfirmed(TX tX, Block block);

  protected int ParseBlockHeader(Block block)
  {
    block.TXs.Clear();
    int startIndex = 0;

    Header header = ParseHeader(block.Buffer, ref startIndex);

    if (block.Header == null)
      block.Header = header;
    else if (block.Header.Hash.IsAllBytesEqual(header.Hash) && block.Header.HashPrevious.IsAllBytesEqual(header.HashPrevious))
      header.CopyTo(block.Header);
    else
      throw new ProtocolException($"Received unexpected block {header} expected was {block.Header}.");

    return startIndex;
  }

  void ParseBlock(Block block, bool verifySignatures)
  {
    int startIndex = ParseBlockHeader(block);

    int countTXs = VarInt.GetInt(block.Buffer, ref startIndex);

    if (countTXs == 0)
      throw new ProtocolException($"Block {block} lacks coinbase transaction.");

    HashSet<byte[]> hashesTX = new(new EqualityComparerByteArray());

    for (int t = 0; t < countTXs; t += 1)
    {
      TX tX = ParseTX(block.Buffer, ref startIndex, isCoinbase: t == 0, verifySignatures);

      if (!hashesTX.Add(tX.Hash))
        throw new ProtocolException($"Block {block.Header.Hash.ToHexString()} contains transaction {tX.Hash.ToHexString()} twice.");

      block.TXs.Add(tX);
    }

    if (!block.Header.MerkleRoot.IsAllBytesEqual(block.ComputeMerkleRoot()))
      throw new ProtocolException("Header merkle root not equal to computed transactions merkle root.");

    DrawAnchorsWinner(block);
  }

  protected virtual void DrawAnchorsWinner(Block block) { }

  protected virtual void ParseBlockStored(Block block)
  {
    ParseBlock(block, verifySignatures: false);
  }

  protected virtual byte[] SerializeBlockStored(Block block)
  {
    return block.Buffer[..block.LengthDataPayload];
  }

  protected Block LoadBlock(int height)
  {
    if (BlocksStored.FindById(height) is BsonDocument bsonDocumentBlock)
      return new Block(bsonDocumentBlock[NAME_FIELD_BLOCK_BYTES].AsBinary);

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

  protected Block LoadBlockParsed(Header header)
  {
    if (LoadBlock(header.Height) is not Block block || !TryParseBlockStored(header, block))
      throw new InvalidOperationException($"Stored block at height {header.Height} cannot be loaded.");

    return block;
  }

  internal Block GetBlock(byte[] hash)
  {
    if (!ChainRoot.TryGetHeader(hash, out Header header) || LoadBlock(header.Height) is not Block block)
      return null;

    int index = 0;

    if (!ParseHeader(block.Buffer, ref index).Hash.IsAllBytesEqual(header.Hash))
      return null;

    block.Header = header;

    return block;
  }

  internal void LoadBlockchain()
  {
    foreach (BsonDocument bsonDocumentBlock in BlocksStored.Find(Query.All(Query.Ascending)))
      try
      {
        Block blockLoad = new(bsonDocumentBlock[NAME_FIELD_BLOCK_BYTES].AsBinary);

        int index = 0;
        Header header = ParseHeader(blockLoad.Buffer, ref index);
        header.Height = bsonDocumentBlock[NAME_FIELD_ID].AsInt32;

        if (!TryParseBlockStored(header, blockLoad))
          continue;

        if (header.HashPrevious.IsAllBytesEqual(ChainRoot.HeaderTipBlockchain.Hash))
          ChainRoot.AppendHeader(header);
        else
          ChainRoot = new(header, isRoot: true);

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
    BsonDocument bsonDocumentBlockOldest = BlocksStored.Query().OrderBy(NAME_FIELD_ID).Select(NAME_FIELD_ID).FirstOrDefault();

    if (bsonDocumentBlockOldest == null)
      return ChainRoot.HeaderRoot.Height;

    return bsonDocumentBlockOldest[NAME_FIELD_ID].AsInt32;
  }

  protected abstract void InsertBlock(Block block);

  protected virtual void RollBackBlock(Block block) { }

  protected virtual void HandleTipUpdated() { }

  internal void ReceiveBlock(Block blockReceived)
  {
    (Header header, DateTime timeRequestLast) itemAwaited = default;

    Chain chainAwaiting = ChainRoot.FindInTree(chain =>
      chain.HeadersAwaitingBlock.Remove(blockReceived.Header.Hash, out itemAwaited) ? chain : null);

    if (chainAwaiting == null)
      return;

    Header headerAwaited = itemAwaited.header;

    Block block = new(blockReceived.Buffer[..blockReceived.LengthDataPayload])
    {
      Header = headerAwaited
    };

    ParseBlock(block, verifySignatures: true);
    chainAwaiting.BlocksQueued.Add(headerAwaited.Height, block);

    UpdateTipBlockchain(chainAwaiting);
  }

  protected void UpdateTipBlockchain(Chain chain)
  {
    if (chain != ChainRoot)
      chain.AdvanceTipBlockchain();

    do
    {
      InsertBlocksQueued();
      ChainRoot.DropForksDeep(GetHeightBlockStoredOldest());
    }
    while (TryReorgToForkStrongest());

    HandleTipUpdated();
  }

  void InsertBlocksQueued()
  {
    while (ChainRoot.BlocksQueued.Remove(ChainRoot.HeaderTipBlockchain.Height + 1, out Block block))
    {
      try
      {
        LiteDatabase.RunTransaction(() =>
        {
          InsertBlock(block);

          MarkTXsOwnedConfirmed(block);

          ChainRoot.HeaderTipBlockchain = block.Header;

          BlocksStored.Upsert(new BsonDocument
          {
            [NAME_FIELD_ID] = block.Header.Height,
            [NAME_FIELD_BLOCK_BYTES] = SerializeBlockStored(block)
          });
        });
      }
      catch (ProtocolException ex)
      {
        RejectBlock(block.Header, ex);
        continue;
      }

      OnBlockInserted.Announce(block);
    }
  }

  protected void RejectBlock(Header header, ProtocolException exception)
  {
    Console.WriteLine(
      $"Block {header.Hash.ToHexString()} at height {header.Height} was rejected: {exception.Message} " +
      $"If the network builds on it, the local database may be corrupt.");

    BlocksStored.DeleteMany(Query.GTE(NAME_FIELD_ID, header.Height));

    ChainRoot.CutOff(header);
  }

  void MarkTXsOwnedConfirmed(Block block)
  {
    foreach (BsonDocument documentTXOwned in TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
      if (IsTXOwnedConfirmed(ParseTXOwned(documentTXOwned), block))
      {
        documentTXOwned[NAME_FIELD_HEIGHT_CONFIRMED] = block.Header.Height;
        TXsOwnedStored.Update(documentTXOwned);
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

  protected void Reorg(Chain chain)
  {
    while (chain != ChainRoot)
    {
      if(chain.ChainTrunk == ChainRoot)
        RollBack(heightAfterRollBack: chain.HeaderRoot.Height - 1);

      chain.SwitchWithTrunk();
      chain = chain.ChainTrunk;
    }
  }

  protected void RollBack(int heightAfterRollBack)
  {
    try
    {
      while (ChainRoot.HeaderTipBlockchain.Height > heightAfterRollBack)
      {
        Header header = ChainRoot.HeaderTipBlockchain;
        Block block = LoadBlockParsed(header);

        LiteDatabase.RunTransaction(() =>
        {
          RollBackBlock(block);

          ReopenTXsOwned(header.Height);

          BlocksStored.Delete(header.Height);
        });

        OnBlockRolledBack.Announce(block);

        ChainRoot.HeaderTipBlockchain = header.HeaderPrevious;

        ChainRoot.BlocksQueued.Add(header.Height, block);
      }
    }
    catch (Exception exception)
    {
      Console.WriteLine(
        $"Rollback of {GetType().Name} to height {heightAfterRollBack} failed, " +
        $"the local database is corrupt and the operator must delete it: {exception}");

      throw;
    }
  }

  void ReopenTXsOwned(int height)
  {
    foreach (BsonDocument documentTXOwned in TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, height)).ToList())
    {
      documentTXOwned[NAME_FIELD_HEIGHT_CONFIRMED] = 0;
      TXsOwnedStored.Update(documentTXOwned);
    }
  }

  internal int GetHeight()
  {
    return ChainRoot.HeaderTipBlockchain.Height;
  }

  internal Header InsertHeadersInTree(List<Header> headers)
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

  protected static T FetchItemMissingNext<T>(
    IEnumerable<T> itemsMissing,
    Dictionary<byte[], (T item, DateTime timeRequestLast)> itemsAwaited,
    Func<T, byte[]> getHash,
    Func<T, bool> canSupplierDeliver)
  {
    foreach (T item in itemsMissing)
      if (canSupplierDeliver(item) && itemsAwaited.TryAdd(getHash(item), (item, DateTime.UtcNow)))
        return item;

    foreach (T item in itemsMissing)
      if (itemsAwaited.TryGetValue(getHash(item), out (T item, DateTime timeRequestLast) itemAwaited)
        && DateTime.UtcNow - itemAwaited.timeRequestLast >= TimeSpan.FromSeconds(SECONDS_DELAY_REQUEST_HEDGED)
        && canSupplierDeliver(item))
      {
        itemsAwaited[getHash(item)] = (item, DateTime.UtcNow);
        return item;
      }

    return default;
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
