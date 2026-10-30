using LiteDB;
using System.Security.Cryptography;
using System.Text;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const long BLOCK_REWARD_INITIAL = 200000000000000; // 200 BTK
  const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

  const long COUNT_SATOSHIS_PER_DAY_MINING = 500000;
  const long TIMESPAN_DAY_SECONDS = 24 * 3600;

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

  internal override TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
  {
    return new TXBToken(buffer, ref index, sHA256, flagIsCoinbase);
  }

  TX CreateTXCoinbase(int blockHeight, long blockReward, byte[] hash160PKeyPublic)
  {
    TXBToken tX = new()
    {
      BlockheightAccountCreated = blockHeight,
    };

    tX.TXOutputs.Add(new TXOutputP2PKH()
    {
      Value = blockReward,
      IDAccount = hash160PKeyPublic,
      Script = [.. TXOutputP2PKH.PREFIX_P2PKH, .. hash160PKeyPublic, .. TXOutputP2PKH.POSTFIX_P2PKH]
    });

    tX.SerializeUnsigned(Wallet.SHA256);

    return tX;
  }

  protected override void InsertBlock(Block block)
  {
    if (!Accounts.Exists())
      return;

    InsertTXs(block);

    if (!SHA256.HashData(Accounts.Serialize()).IsAllBytesEqual(((HeaderBToken)block.Header).HashDatabase))
      throw new ProtocolException($"Hash of DB not equal to HashDatabase of block {block.Header.Hash.ToHexString()}.");
  }

  internal void InsertTXs(Block block)
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

  byte[] GetGenesisBlockBytes()
  {
    return [
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x3b, 0xa3, 0xed, 0xfd, 0x7a, 0x7b, 0x12, 0xb2, 0x7a, 0xc7, 0x2c, 0x3e,
        0x67, 0x76, 0x8f, 0x61, 0x7f, 0xc8, 0x1b, 0xc3, 0x88, 0x8a, 0x51, 0x32, 0x3a, 0x9f, 0xb8, 0xaa,
        0x4b, 0x1e, 0x5e, 0x4a, 0x29, 0xab, 0x5f, 0x49, 0xff, 0xff, 0x00, 0x1d, 0x1d, 0xac, 0x2b, 0x7c,
        0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x4d, 0x04, 0xff, 0xff, 0x00, 0x1d,
        0x01, 0x04, 0x45, 0x54, 0x68, 0x65, 0x20, 0x54, 0x69, 0x6d, 0x65, 0x73, 0x20, 0x30, 0x33, 0x2f,
        0x4a, 0x61, 0x6e, 0x2f, 0x32, 0x30, 0x30, 0x39, 0x20, 0x43, 0x68, 0x61, 0x6e, 0x63, 0x65, 0x6c,
        0x6c, 0x6f, 0x72, 0x20, 0x6f, 0x6e, 0x20, 0x62, 0x72, 0x69, 0x6e, 0x6b, 0x20, 0x6f, 0x66, 0x20,
        0x73, 0x65, 0x63, 0x6f, 0x6e, 0x64, 0x20, 0x62, 0x61, 0x69, 0x6c, 0x6f, 0x75, 0x74, 0x20, 0x66,
        0x6f, 0x72, 0x20, 0x62, 0x61, 0x6e, 0x6b, 0x73, 0xff, 0xff, 0xff, 0xff, 0x01, 0x00, 0xf2, 0x05,
        0x2a, 0x01, 0x00, 0x00, 0x00, 0x43, 0x41, 0x04, 0x67, 0x8a, 0xfd, 0xb0, 0xfe, 0x55, 0x48, 0x27,
        0x19, 0x67, 0xf1, 0xa6, 0x71, 0x30, 0xb7, 0x10, 0x5c, 0xd6, 0xa8, 0x28, 0xe0, 0x39, 0x09, 0xa6,
        0x79, 0x62, 0xe0, 0xea, 0x1f, 0x61, 0xde, 0xb6, 0x49, 0xf6, 0xbc, 0x3f, 0x4c, 0xef, 0x38, 0xc4,
        0xf3, 0x55, 0x04, 0xe5, 0x1e, 0xc1 ,0x12, 0xde, 0x5c, 0x38, 0x4d, 0xf7, 0xba, 0x0b, 0x8d, 0x57,
        0x8a, 0x4c, 0x70, 0x2b, 0x6b, 0xf1, 0x1d, 0x5f, 0xac, 0x00, 0x00 ,0x00 ,0x00 ];
  }

  internal override HeaderBToken ParseHeader(byte[] buffer, ref int index, SHA256 sHA256)
  {
    byte[] hash =
      sHA256.ComputeHash(
        sHA256.ComputeHash(
          buffer,
          index,
          HeaderBToken.COUNT_HEADER_BYTES));

    uint version = BitConverter.ToUInt32(buffer, index);
    index += 4;

    byte[] hashHeaderPrevious = new byte[32];
    Array.Copy(buffer, index, hashHeaderPrevious, 0, 32);
    index += 32;

    byte[] merkleRootHash = new byte[32];
    Array.Copy(buffer, index, merkleRootHash, 0, 32);
    index += 32;

    byte[] hashDatabase = new byte[32];
    Array.Copy(buffer, index, hashDatabase, 0, 32);
    index += 32;

    uint nonce = BitConverter.ToUInt32(buffer, index);
    index += 4;

    return new HeaderBToken(
      hash,
      hashHeaderPrevious,
      merkleRootHash,
      hashDatabase,
      nonce);
  }
}
