using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  partial class TokenBToken : Token
  {
    const int COUNT_BLOCKS_DOWNLOAD_DEPTH_MAX = 1000;

    const long BLOCK_REWARD_INITIAL = 200000000000000; // 200 BTK
    const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

    const int SIZE_BUFFER_BLOCK = 0x400000;

    const long COUNT_SATOSHIS_PER_DAY_MINING = 500000;
    const long TIMESPAN_DAY_SECONDS = 24 * 3600;

    const UInt16 COMPORT_BTOKEN = 8777;

    DatabaseAccounts DatabaseAccounts = new();
 

    public TokenBToken(Token tokenParent)
      : base(
          COMPORT_BTOKEN,
          flagEnableInboundConnections: true)
    {
      TokenParent = tokenParent;
      tokenParent.TokenChild = this;

      HeaderGenesis.HeaderParent = tokenParent.HeaderGenesis;

      PathBlocksMinedUnconfirmed = Path.Combine(
        GetName(),
        "BlocksMinedUnconfirmed");

      Directory.CreateDirectory(PathBlocksMinedUnconfirmed);
      LoadMinedUnconfirmed();
    }

    void LoadMinedUnconfirmed()
    {
      foreach (string pathFile in Directory.GetFiles(PathBlocksMinedUnconfirmed))
      {
        try
        {
          BlockBToken block = new();
          block.Parse(File.ReadAllBytes(pathFile));
          BlocksMined.Add(block);

          //TokensAnchorMinedUnconfirmed.Add(tokenAnchor);
        }
        catch (Exception ex)
        {
          $"Failed to parse unconfirmed mined block {pathFile}.\n{ex.Message}".Log(LogFile);
        }
      }
    }

    public override Header CreateHeaderGenesis()
    {
      HeaderBToken header = new(
        headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
        hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
        merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
        unixTimeSeconds: 1231006505,
        nonce: 0);

      header.DifficultyAccumulated = header.Difficulty;

      return header;
    }

    public override void LoadImageDatabase(string pathImage)
    {
      DatabaseAccounts.LoadImage(pathImage);
    }

    public override void CreateImageDatabase(string pathImage)
    {
      DatabaseAccounts.CreateImage(pathImage);
    }

    protected override void InsertInDatabase(Block block)
    {
      $"Insert BToken block {block} in database.".Log(LogFile);

      DatabaseAccounts.InsertBlock((BlockBToken)block);

      long outputValueTXCoinbase = 0;
      block.TXs[0].TXOutputs.ForEach(o => outputValueTXCoinbase += o.Value);

      long blockReward = BLOCK_REWARD_INITIAL >>
        block.Header.Height / PERIOD_HALVENING_BLOCK_REWARD;

      if (blockReward + block.Fee != outputValueTXCoinbase)
        throw new ProtocolException(
          $"Output value of Coinbase TX {block.TXs[0]}\n" +
          $"does not add up to block reward {blockReward} plus block fee {block.Fee}.");
    }

    public override void InsertDB(
      byte[] bufferDB,
      int lengthDataInBuffer)
    {
      DatabaseAccounts.InsertDB(bufferDB, lengthDataInBuffer);
    }

    public override void DeleteDB()
    { 
      DatabaseAccounts.Delete(); 
    }
        
    public override void RevokeBlockInsertion()
    {
      TokensAnchorDetectedInBlock.Clear();
    }

    public override List<byte[]> ParseHashesDB(
      byte[] buffer,
      int length,
      Header headerTip)
    {
      SHA256 sHA256 = SHA256.Create();

      byte[] hashRootHashesDB = sHA256.ComputeHash(
        buffer,
        0,
        length);

      if (!((HeaderBToken)headerTip).HashDatabase.IsEqual(hashRootHashesDB))
        throw new ProtocolException(
          $"Root hash of hashesDB not equal to database hash in header tip");

      List<byte[]> hashesDB = new();

      for (
        int i = 0;
        i < DatabaseAccounts.COUNT_CACHES + DatabaseAccounts.COUNT_FILES_DB;
        i += 32)
      {
        byte[] hashDB = new byte[32];
        Array.Copy(buffer, i, hashDB, 0, 32);
        hashesDB.Add(hashDB);
      }

      return hashesDB;
    }

    public override Block CreateBlock()
    {
      return new BlockBToken(SIZE_BUFFER_BLOCK);
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

    public override void Reset()
    {
      base.Reset();
      DatabaseAccounts.ClearCache();
    }

    public override bool TryGetDB(
      byte[] hash,
      out byte[] dataDB)
    {
      return DatabaseAccounts.TryGetDB(hash, out dataDB);
    }

    public override List<string> GetSeedAddresses()
    {
      return new List<string>()
      {
        "83.229.86.158"
      };
    }

    public override bool FlagDownloadDBWhenSync(HeaderDownload h)
    {
      return
        h.HeaderTip != null
        &&
        (DatabaseAccounts.GetCountBytes() <
        h.HeaderTip.CountBytesBlocksAccumulated - h.HeaderRoot.CountBytesBlocksAccumulated
        ||
        COUNT_BLOCKS_DOWNLOAD_DEPTH_MAX <
        h.HeaderTip.Height - h.HeaderRoot.Height);
    }
  }
}
