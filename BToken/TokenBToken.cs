using System.Security.Cryptography;


namespace BTokenCore;

public partial class TokenBToken : Token
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const long BLOCK_REWARD_INITIAL = 200000000000000; // 200 BTK
  const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

  const long COUNT_SATOSHIS_PER_DAY_MINING = 500000;
  const long TIMESPAN_DAY_SECONDS = 24 * 3600;

  internal DatabaseAccounts Accounts = new();

  internal PoolTXBToken TXPool;

  public TokenBToken()
  {
    TXPool = new PoolTXBToken(this);

    SizeBlockMax = SIZE_BLOCK_MAX;
  }

  public override Header CreateHeaderGenesis()
  {
    HeaderBToken header = new(
      headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
      hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
      hashDatabase: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
      nonce: 0);

    return header;
  }

  internal Header CreateHeaderPlaceholder(TXOutputTokenAnchor anchor, Header headerParent)
  {
    return new HeaderBToken(
      headerHash: anchor.HashBlockReferenced,
      hashPrevious: anchor.HashBlockPreviousReferenced,
      merkleRootHash: new byte[32],
      hashDatabase: new byte[32],
      nonce: 0)
    {
      HeaderParent = headerParent
    };
  }

  public override TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
  {
    return new TXBToken(buffer, ref index, sHA256, flagIsCoinbase);
  }

  internal Block MineBlock(Header headerPrevious, Block block)
  {
    int height = headerPrevious.Height + 1;

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
      HashPrevious = headerPrevious.Hash,
      MerkleRoot = block.ComputeMerkleRoot(),
      CountTXs = block.TXs.Count,
      Fee = feeTXs
    };

    return block;
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
      Script = [.. PREFIX_P2PKH, .. hash160PKeyPublic, .. POSTFIX_P2PKH]
    });

    tX.SerializeUnsigned(Wallet.SHA256);

    return tX;
  }

  internal override bool TryGetTX(byte[] hash, out TX tX)
  {
    tX = null;

    if (!TXPool.TXsByHash.TryGetValue(hash, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem))
      return false;

    tX = tXsByHashItem.tX;
    return true;
  }

  public override void InsertBlock(Block block)
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

  public override void RollBack(Block block)
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

  public override HeaderBToken ParseHeader(byte[] buffer, ref int index, SHA256 sHA256)
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
