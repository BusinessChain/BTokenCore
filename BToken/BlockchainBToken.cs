using LiteDB;
using System.Security.Cryptography;
using System.Text;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  internal DatabaseAccounts Accounts = new();

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

    blockchainParent.OnBlockInserted += InsertHeaderPlaceholder;
    blockchainParent.OnBlockInserted += TriggerBootstrap;
    blockchainParent.OnBlockRolledBack += RemoveHeaderPlaceholder;

    Encoding.ASCII.GetBytes("BTK").CopyTo(IDToken, 0);

    if (!Accounts.TryLoad(Database))
      DatabaseBlockCollection.DeleteAll();
  }

  protected override Header CreateHeaderGenesis()
  {
    HeaderBToken header = new(
      headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
      hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
      hashDatabase: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      nonce: 0);

    return header;
  }

  internal override HeaderBToken ParseHeader(byte[] buffer, ref int index, SHA256 sHA256)
  {
    return new HeaderBToken(buffer, ref index, sHA256);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
  {
    return new TXBToken(buffer, ref index, sHA256, flagIsCoinbase);
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

  protected override void InsertBlock(Block block)
  {
    if (!Accounts.Exists())
      return;

    InsertTXs(block);

    if (!SHA256.HashData(Accounts.Serialize()).IsAllBytesEqual(((HeaderBToken)block.Header).HashDatabase))
      throw new ProtocolException($"Hash of DB not equal to HashDatabase of block {block.Header.Hash.ToHexString()}.");
  }

  void InsertTXs(Block block)
  {
    for (int i = 0; i < block.TXs.Count; i += 1)
    {
      TXBToken tX = (TXBToken)block.TXs[i];

      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput.Value > 0)
          AddToBalance(tXOutput.IDAccount, block.Header.Height, tXOutput.Value);

      if (i > 0)
        SpendTXInput(tX);
    }
  }

  void AddToBalance(byte[] accountID, int blockHeightAccountCreated, long value)
  {
    Account account = Accounts.LoadAccount(accountID, blockHeightAccountCreated);
    account.Balance += value;
    Accounts.WriteAccount(account);
  }

  void SpendTXInput(TXBToken tX)
  {
    Account account = Accounts.GetCopyOfAccount(tX.IDAccountSource);
    account.SpendTX(tX);
    Accounts.WriteAccount(account);
  }

  protected override void RollBackBlock(Block block)
  {
    if (!Accounts.Exists())
      return;

    for (int i = block.TXs.Count - 1; i >= 0; i--)
    {
      TXBToken tX = block.TXs[i] as TXBToken;

      if (i > 0)
      {
        Account accountSource = Accounts.LoadAccount(tX.IDAccountSource, tX.BlockheightAccountCreated);
        accountSource.ReverseSpendTX(tX);
        Accounts.WriteAccount(accountSource);
      }

      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput.Value > 0)
          AddToBalance(tXOutput.IDAccount, block.Header.Height, -tXOutput.Value);
    }
  }

  internal override Task<byte[]> GetDB(byte[] hash)
  {
    return RunLocked(() =>
    {
      if (ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(hash) && Accounts.Exists())
        return Accounts.Serialize();

      return null;
    });
  }

  internal override Task<byte[]> GetHashDBMissing()
  {
    return RunLocked(() =>
    {
      if (!Accounts.Exists())
        return ChainRoot.HeaderTipBlockchain.Hash;

      return null;
    });
  }

  internal Task InstallDB(byte[] hash, byte[] buffer, int length)
  {
    return RunLocked(() =>
    {
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
    });
  }

  void TriggerBootstrap(Block blockParent)
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

  internal Task AddTX(TX tX)
  {
    return RunLocked(() => TXPool.AddTX(tX));
  }

  internal Task<TX> GetTX(byte[] hash)
  {
    return RunLocked<TX>(() =>
    {
      TXPool.TXsByHash.TryGetValue(hash, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem);

      return tXsByHashItem.tX;
    });
  }

  internal Task<List<byte[]>> GetHashesTXNotInPool(List<byte[]> hashes)
  {
    return RunLocked(() => hashes.Where(h => !TXPool.TXsByHash.ContainsKey(h)).ToList());
  }

  internal Block MineBlock(out TXOutputTokenAnchor anchorToken)
  {
    Block block = TakeBlockFromPool();

    int height = ChainRoot.HeaderTipBlockchain.Height + 1;

    block.TXs = TXPool.GetTXs(block.Buffer.Length);

    long feeTXs = block.TXs.Sum(t => t.Fee);

    long blockReward = HeaderBToken.GetBlockReward(height) + feeTXs;

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

  TX CreateTXCoinbase(int blockHeight, long blockReward, byte[] hash160PKeyPublic)
  {
    TXBToken tX = new()
    {
      BlockheightAccountCreated = blockHeight,
    };

    tX.TXOutputs.Add(new TXOutputP2PKH(blockReward, hash160PKeyPublic));

    tX.SerializeUnsigned(Wallet.SHA256);

    return tX;
  }

  internal void InsertBlockMined(Block blockParent, Block block)
  {
    if (!ChainRoot.TryGetHeaderPlaceholder(blockParent.Header, out Header headerPlaceholder))
      return;

    QueueBlock(headerPlaceholder, block);
  }

  void InsertHeaderPlaceholder(Block blockParent)
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

    Block block = TakeBlockFromPool();

    if (!TryLoadBlock(headerPlaceholder.Height, block))
    {
      PoolBlocks.Add(block);
      return;
    }

    QueueBlock(headerPlaceholder, block);
  }

  void QueueBlock(Header header, Block block)
  {
    if (!TryParseBlockStored(header, block))
    {
      PoolBlocks.Add(block);
      return;
    }

    Chain chain = ChainRoot.FindChain(header);
    chain.Blocks.Add(header.Height, block);

    UpdateTipBlockchain(chain);
  }

  void RemoveHeaderPlaceholder(Block blockParent)
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
}
