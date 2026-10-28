using LiteDB;


namespace BTokenCore;

internal class BlockchainBToken : Blockchain
{
  IDatabaseToken DatabaseToken;


  internal BlockchainBToken(
    TokenBToken token,
    BlockchainBitcoin blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base(token, blockchainParent, semaphoreBlockchain)
  {
    DatabaseToken = Token.GetDatabase();

    if (!DatabaseToken.TryLoad(Database))
      DatabaseBlockCollection.DeleteAll();
  }

  protected override void ParseBlockStored(Block block)
  {
    block.Parse();
  }

  protected override byte[] SerializeBlockStored(Block block)
  {
    DatabaseBlockCollection.DeleteMany(
      Query.LT("_id", ChainRoot.GetHeightPruning()));

    return block.Buffer[..block.LengthDataPayload];
  }

  internal override async Task<byte[]> GetDB(byte[] hash)
  {
    try
    {
      await LockBlockchain();

      if (ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(hash) && DatabaseToken.Exists())
        return DatabaseToken.Serialize();

      return null;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal override async Task<byte[]> GetHashDBMissing()
  {
    try
    {
      await LockBlockchain();

      if (!DatabaseToken.Exists())
        return ChainRoot.HeaderTipBlockchain.Hash;

      return null;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task InstallDB(byte[] hash, byte[] buffer, int length)
  {
    try
    {
      await LockBlockchain();

      if (DatabaseToken.Exists()
        || !ChainRoot.TryGetHeader(hash, out Header header)
        || header.Height > ChainRoot.HeaderTipBlockchain.Height)
        return;

      RollBack(heightAfterRollBack: header.Height);

      Database.BeginTrans();

      try
      {
        DatabaseToken.Install(buffer, length);
      }
      catch
      {
        Database.Rollback();
        DatabaseToken.Drop();
        throw;
      }

      Database.Commit();

      UpdateTipBlockchain(ChainRoot);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal Block MineBlock(out TXOutputTokenAnchor anchorToken)
  {
    return Token.MineBlock(ChainRoot.HeaderTipBlockchain, TakeBlockFromPool(), out anchorToken);
  }

  internal void RollBackUnanchored()
  {
    Header header = ChainRoot.HeaderRoot;

    while (header != null && header.HeaderParent == null)
      header = header.HeaderNext;

    while (header != null && header.HeaderParent != null)
      header = header.HeaderNext;

    if (header != null)
      RemoveHeader(ChainRoot, header);
  }

  internal void InsertHeaderPlaceholder(Block blockParent)
  {
    if (!blockParent.Header.AnchorsWinner.TryGetValue(Token.IDToken, out TXOutputTokenAnchor anchorWinner))
      return;

    Header headerPlaceholder = Token.CreateHeaderPlaceholder(anchorWinner, blockParent.Header);

    Header headerLastInTree = ChainRoot.HeaderRoot;

    if (!headerLastInTree.Hash.IsAllBytesEqual(headerPlaceholder.Hash))
      ChainRoot.InsertHeadersInTree([headerPlaceholder], GetHeightBlockStoredOldest(), out headerLastInTree);

    if (headerLastInTree == null)
      return;

    if (headerLastInTree != headerPlaceholder)
    {
      headerLastInTree.HeaderParent = blockParent.Header;
      return;
    }

    if (!TryLoadBlock(headerPlaceholder.Height, out Block block))
      return;

    try
    {
      block.Header = headerPlaceholder;
      ParseBlockStored(block);
    }
    catch (ProtocolException)
    {
      PoolBlocks.Add(block);
      return;
    }

    Chain chain = ChainRoot.FindChain(headerPlaceholder);
    chain.Blocks.Add(headerPlaceholder.Height, block);

    UpdateTipBlockchain(chain);
  }

  internal void TriggerBootstrap(Block blockParent)
  {
    bool isBlockParentTip = blockParent.Header.HeaderNext == null;

    if (isBlockParentTip && ChainRoot.HeaderTip.Height - ChainRoot.HeaderTipBlockchain.Height > MAX_COUNT_BLOCKS_STORED)
    {
      DatabaseToken.Drop();
      DatabaseBlockCollection.DeleteAll();

      Header headerRoot = ChainRoot.HeaderTip;

      while (headerRoot.Height > ChainRoot.HeaderTip.Height - MAX_COUNT_BLOCKS_STORED)
        headerRoot = headerRoot.HeaderPrevious;

      ChainRoot.ReRoot(headerRoot, PoolBlocks);

      UpdateTipBlockchain(ChainRoot);
    }
  }

  internal void RemoveHeaderPlaceholder(Block blockParent)
  {
    if (!ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder))
      return;

    RemoveHeader(ChainRoot.FindChain(headerPlaceholder), headerPlaceholder);
  }

  void RemoveHeader(Chain chain, Header header)
  {
    if (chain == ChainRoot)
      RollBack(heightAfterRollBack: header.Height - 1);

    chain.CutOff(header, PoolBlocks);

    UpdateTipBlockchain(ChainRoot);
  }
}
