using LiteDB;
using System.Security.Cryptography;
using System.Text;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  PoolTXBToken TXPool;

  internal byte[] IDToken = new byte[TXOutputTokenAnchor.LENGTH_ID_TOKEN];


  internal BlockchainBToken(
    BlockchainBitcoin blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base("TokenBToken", blockchainParent, semaphoreBlockchain)
  {
    SizeBlockMax = SIZE_BLOCK_MAX;

    TXPool = new PoolTXBToken(this);

    OnBlockInserted += TXPool.RemoveTXs;
    OnBlockRolledBack += TXPool.ReturnTXs;

    Encoding.ASCII.GetBytes("BTK").CopyTo(IDToken, 0);

    if (!Accounts.TryLoad(Database))
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

      if (ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(hash) && Accounts.Exists())
        return Accounts.Serialize();

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

      if (!Accounts.Exists())
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

      if (Accounts.Exists()
        || !ChainRoot.TryGetHeader(hash, out Header header)
        || header.Height > ChainRoot.HeaderTipBlockchain.Height)
        return;

      RollBack(heightAfterRollBack: header.Height);

      Database.BeginTrans();

      try
      {
        Accounts.Install(buffer, length);
      }
      catch
      {
        Database.Rollback();
        Accounts.Drop();
        throw;
      }

      Database.Commit();

      RollBack(heightAfterRollBack: GetHeightBlockStoredOldest());

      if (!SHA256.HashData(Accounts.Serialize())
        .IsAllBytesEqual(((HeaderBToken)ChainRoot.HeaderTipBlockchain).HashDatabase))
      {
        Accounts.Drop();
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

  internal async Task AddTX(TX tX)
  {
    try
    {
      await LockBlockchain();

      TXPool.AddTX(tX);
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<TX> GetTX(byte[] hash)
  {
    try
    {
      await LockBlockchain();

      TXPool.TXsByHash.TryGetValue(hash, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem);

      return tXsByHashItem.tX;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal async Task<List<byte[]>> GetHashesTXNotInPool(List<byte[]> hashes)
  {
    try
    {
      await LockBlockchain();

      return hashes.Where(h => !TXPool.TXsByHash.ContainsKey(h)).ToList();
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  internal Block MineBlock(out TXOutputTokenAnchor anchorToken)
  {
    Block block = TakeBlockFromPool();

    int height = ChainRoot.HeaderTipBlockchain.Height + 1;

    block.TXs = TXPool.GetTXs(block.Buffer.Length);

    long feeTXs = block.TXs.Sum(t => t.Fee);

    long blockReward =
      (BLOCK_REWARD_INITIAL >> height / PERIOD_HALVENING_BLOCK_REWARD)
      + feeTXs;

    TX tXCoinbase = CreateTXCoinbase(height, blockReward, Wallet.Hash160PKeyPublic);

    block.TXs.Insert(0, tXCoinbase);

    block.Header = new HeaderBToken()
    {
      Height = height,
      HashPrevious = ChainRoot.HeaderTipBlockchain.Hash,
      MerkleRoot = block.ComputeMerkleRoot(),
      CountTXs = block.TXs.Count,
      Fee = feeTXs
    };

    Database.BeginTrans();

    try
    {
      InsertTXs(block);
      ((HeaderBToken)block.Header).HashDatabase = SHA256.HashData(Accounts.Serialize());
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

    Header headerPlaceholder = new HeaderBToken(
      headerHash: anchorWinner.HashBlockReferenced,
      hashPrevious: anchorWinner.HashBlockPreviousReferenced,
      merkleRootHash: new byte[32],
      hashDatabase: new byte[32],
      nonce: 0)
    {
      HeaderParent = blockParent.Header
    };

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
      Accounts.Drop();
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
