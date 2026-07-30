using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Security.Cryptography;

using LiteDB;


namespace BTokenCore;

public partial class TokenBToken : Token
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const long BLOCK_REWARD_INITIAL = 200000000000000; // 200 BTK
  const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

  const long COUNT_SATOSHIS_PER_DAY_MINING = 500000;
  const long TIMESPAN_DAY_SECONDS = 24 * 3600;

  Dictionary<byte[], Account> AccountsStaged = new(new EqualityComparerByteArray());

  LiteDatabase Database;
  ILiteCollection<Account> DatabaseAccountCollection;
  ILiteCollection<BsonDocument> DatabaseMetaCollection;

  PoolTXBToken TXPool;

  string PathRootDB;
  public const int COUNT_FILES_DB = 256;
  byte[] HashesFilesDB = new byte[COUNT_FILES_DB * 32];


  public TokenBToken(ILogEntryNotifier logEntryNotifier, Token tokenParent)
    : base(logEntryNotifier)
  {
    TXPool = new PoolTXBToken(this);

    SizeBlockMax = SIZE_BLOCK_MAX;

    Database = new LiteDatabase($"Filename={GetName()}.db;Mode=Exclusive");
    DatabaseAccountCollection = Database.GetCollection<Account>("accounts");
    DatabaseMetaCollection = Database.GetCollection<BsonDocument>("meta");

    AppDomain.CurrentDomain.ProcessExit += (s, e) => { Database?.Dispose(); };

    IDToken = new byte[3] { (byte)'B', (byte)'T', (byte)'K' };

    Network = new NetworkToken(
      tokenParent,
      this,
      port: 8777,
      flagEnableInboundConnections: true,
      flagEnableRelay: true);
  }

  public void Start()
  {
    Network.Start();
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

  public override TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
  {
    return new TXBToken(buffer, ref index, sHA256, flagIsCoinbase);
  }

  public override Block MineBlock(int height, out TXOutputTokenAnchor anchorToken)
  {
    Block block = new Block(this);

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
      MerkleRoot = block.ComputeMerkleRoot(),
      CountTXs = block.TXs.Count,
      Fee = feeTXs
    };

    anchorToken = new TXOutputTokenAnchor()
    {
      HashBlockPreviousReferenced = block.Header.HashPrevious,
      HashBlockReferenced = block.Header.Hash
    };

    return block;
  }


  const int LENGTH_TX_P2PKH = 120;

  public override bool TryCreateTXAnchor(
    TXOutputTokenAnchor tokenAnchor,
    long feePerByte,
    out TX tXAnchor)
  {
    tXAnchor = null;
    byte[] dataAnchorToken = tokenAnchor.Serialize();

    long fee = feePerByte * LENGTH_TX_P2PKH;

    Account accountWallet = null; // Aus DB holen hier

    if (accountWallet == null || accountWallet.Balance < fee)
      return false;

    TXBToken tX = new()
    {
      KeyPublic = Wallet.KeyPublic,
      BlockheightAccountCreated = accountWallet.BlockHeightAccountCreated,
      Nonce = accountWallet.Nonce,
      Fee = fee
    };

    tX.TXOutputs.Add(tokenAnchor);

    tX.Serialize(Wallet);

    TXPool.AddTX(tX);

    return true;
  }

  TX CreateTXCoinbase(int blockHeight, long blockReward, byte[] hash160PKeyPublic)
  {
    TXBToken tX = new()
    {
      KeyPublic = new byte[32],
      BlockheightAccountCreated = blockHeight,
    };

    TXOutputP2PKH tXOutput = new()
    {
      Type = TXOutput.TypesToken.P2PKH,
      Script = BitConverter.GetBytes(blockReward).Concat(hash160PKeyPublic).ToArray()
    };

    tX.Serialize(Wallet);

    return tX;
  }

  public override bool TryGetTX(byte[] hash, out TX tX)
  {
    tX = null;
    return false;
  }

  public override void InsertBlock(Block block)
  {
    try
    {
      for (int i = 0; i < block.TXs.Count; i += 1)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        foreach (TXOutput tXOutput in tX.TXOutputs)
          StageInsertTXOutput(tXOutput, block.Header.Height);

        if (i > 0)
          StageSpendTXInput(tX);
      }

      foreach (Account account in AccountsStaged.Values)
        if (account.Balance > 0)
          DatabaseAccountCollection.Upsert(account);
        else
          DatabaseAccountCollection.Delete(account.ID);

      TXPool.RemoveTXs(block.TXs.Select(tX => tX.Hash));
    }
    finally
    {
      AccountsStaged.Clear();
    }
  }

  int SerialNumberTX;

  protected void StageInsertTXOutput(TXOutput tXOutput, int blockHeight)
  {
    if (tXOutput.Value < 0)
      throw new ProtocolException($"Value of TX output {tXOutput.IDAccount.ToHexString()} smaller than zero.");

    if (tXOutput.Value > 0)
    {
      if (AccountsStaged.TryGetValue(tXOutput.IDAccount, out Account accountStaged))
        accountStaged.Balance += tXOutput.Value;
      else
      {
        if (DatabaseAccountCollection.FindById(tXOutput.IDAccount) is Account accountStored)
          accountStaged = new()
          {
            ID = accountStored.ID,
            BlockHeightAccountCreated = accountStored.BlockHeightAccountCreated,
            Nonce = accountStored.Nonce,
            Balance = accountStored.Balance + tXOutput.Value
          };
        else
          accountStaged = new()
          {
            ID = tXOutput.IDAccount,
            BlockHeightAccountCreated = blockHeight,
            Nonce = 0,
            Balance = tXOutput.Value
          };

        AccountsStaged.Add(accountStaged.ID, accountStaged);
      }
    }
  }

  Account GetCopyOfAccount(byte[] accountID)
  {
    if (DatabaseAccountCollection.FindById(accountID) is Account accountStored)
      return new(accountStored);
    else
      throw new ProtocolException($"Account {accountID.ToHexString()} not found in database.");
  }

  protected void StageSpendTXInput(TX tX)
  {
    var tXBToken = tX as TXBToken;

    Account accountStaged = GetCopyOfAccount(tXBToken.IDAccountSource);
    AccountsStaged.Add(accountStaged.ID, accountStaged);

    accountStaged.SpendTX(tXBToken);
  }

  public override void ReverseBlock(Block block)
  {
    try
    {
      for (int i = block.TXs.Count - 1; i >= 0; i--)
      {
        TXBToken tX = block.TXs[i] as TXBToken;

        if (i > 0)
          ReverseSpendInputInDB(tX);

        foreach (TXOutput output in tX.TXOutputs)
          ReverseOutputInDB(output);
      }
    }
    finally
    {
      AccountsStaged.Clear();
    }
  }

  void ReverseSpendInputInDB(TXBToken tX)
  {

  }

  void ReverseOutputInDB(TXOutput tXOutput)
  {

  }

  public List<byte[]> ParseHashesDB(byte[] buffer, int length, Header headerTip)
  {
    SHA256 sHA256 = SHA256.Create();

    byte[] hashRootHashesDB = sHA256.ComputeHash(buffer, 0, length);

    if (!((HeaderBToken)headerTip).HashDatabase.IsAllBytesEqual(hashRootHashesDB))
      throw new ProtocolException($"Root hash of hashesDB not equal to database hash in header tip");

    List<byte[]> hashesDB = new();

    return hashesDB;
  }

  byte[] GetGenesisBlockBytes()
  {
    return new byte[285]{
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
        0x8a, 0x4c, 0x70, 0x2b, 0x6b, 0xf1, 0x1d, 0x5f, 0xac, 0x00, 0x00 ,0x00 ,0x00 };
  }

  public override List<string> GetSeedAddresses()
  {
    return new List<string>()
      {
        "83.229.86.158" 
        //84.74.69.100
      };
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
