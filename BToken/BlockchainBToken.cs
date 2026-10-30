using LiteDB;
using System.Security.Cryptography;
using System.Text;


namespace BTokenCore;

internal class BlockchainBToken : Blockchain
{
  TokenBToken TokenBToken;

  internal byte[] IDToken = new byte[Token.LENGTH_ID_TOKEN];


  internal BlockchainBToken(
    TokenBToken token,
    BlockchainBitcoin blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base(token, blockchainParent, semaphoreBlockchain)
  {
    TokenBToken = token;

    Encoding.ASCII.GetBytes("BTK").CopyTo(IDToken, 0);

    if (!TokenBToken.Accounts.TryLoad(Database))
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

      if (ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(hash) && TokenBToken.Accounts.Exists())
        return TokenBToken.Accounts.Serialize();

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

      if (!TokenBToken.Accounts.Exists())
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

      if (TokenBToken.Accounts.Exists()
        || !ChainRoot.TryGetHeader(hash, out Header header)
        || header.Height > ChainRoot.HeaderTipBlockchain.Height)
        return;

      RollBack(heightAfterRollBack: header.Height);

      Database.BeginTrans();

      try
      {
        TokenBToken.Accounts.Install(buffer, length);
      }
      catch
      {
        Database.Rollback();
        TokenBToken.Accounts.Drop();
        throw;
      }

      Database.Commit();

      RollBack(heightAfterRollBack: GetHeightBlockStoredOldest());

      if (!SHA256.HashData(TokenBToken.Accounts.Serialize())
        .IsAllBytesEqual(((TokenBToken.HeaderBToken)ChainRoot.HeaderTipBlockchain).HashDatabase))
      {
        TokenBToken.Accounts.Drop();
        UpdateTipBlockchain(ChainRoot);

        throw new ProtocolException($"Hash of DB not equal to HashDatabase of oldest stored block.");
      }

      UpdateTipBlockchain(ChainRoot);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<TX> AddTX(byte[] tXRaw)
  {
    try
    {
      await LockBlockchain();

      int index = 0;

      TX tX = TokenBToken.ParseTX(tXRaw, ref index, TokenBToken.Wallet.SHA256, flagIsCoinbase: false);

      TokenBToken.TXPool.AddTX(tX);

      return tX;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal Block MineBlock(out TXOutputTokenAnchor anchorToken)
  {
    Block block = TokenBToken.MineBlock(ChainRoot.HeaderTipBlockchain, TakeBlockFromPool());

    Database.BeginTrans();

    try
    {
      TokenBToken.InsertTXs(block);
      ((TokenBToken.HeaderBToken)block.Header).HashDatabase = SHA256.HashData(TokenBToken.Accounts.Serialize());
    }
    finally
    {
      Database.Rollback();
    }

    block.Header.ComputeHash();

    anchorToken = new TXOutputTokenAnchor()
    {
      IDToken = IDToken,
      HashBlockPreviousReferenced = block.Header.HashPrevious,
      HashBlockReferenced = block.Header.Hash
    };

    return block;
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
    if (!blockParent.Header.AnchorsWinner.TryGetValue(IDToken, out TXOutputTokenAnchor anchorWinner))
      return;

    Header headerPlaceholder = TokenBToken.CreateHeaderPlaceholder(anchorWinner, blockParent.Header);

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

    QueueBlock(headerPlaceholder, block);
  }

  internal void InsertBlockMined(Block blockParent, Block block)
  {
    if (!ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder))
      return;

    QueueBlock(headerPlaceholder, block);
  }

  void QueueBlock(Header header, Block block)
  {
    try
    {
      block.Header = header;
      ParseBlockStored(block);
    }
    catch (ProtocolException)
    {
      PoolBlocks.Add(block);
      return;
    }

    Chain chain = ChainRoot.FindChain(header);
    chain.Blocks.Add(header.Height, block);

    UpdateTipBlockchain(chain);
  }

  internal void TriggerBootstrap(Block blockParent)
  {
    bool isBlockParentTip = blockParent.Header.HeaderNext == null;

    if (isBlockParentTip && ChainRoot.HeaderTip.Height - ChainRoot.HeaderTipBlockchain.Height > MAX_COUNT_BLOCKS_STORED)
    {
      TokenBToken.Accounts.Drop();
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
