using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;


using BTokenLib;

namespace BTokenCore
{
  // compose token of componenents like PoW, dPow, UTXO, Parser
  // exposes IHeader for the blockchain Syncer

  class TokenBitcoin : Token
  {
    Dictionary<int, byte[]> Checkpoints = new()
    {
      { 11111, "0000000069e244f73d78e8fd29ba2fd2ed618bd6fa2ee92559f542fdb26e7c1d".ToBinary() },
      { 250000, "000000000000003887df1f29024b06fc2200b55f8af8f35453d7be294df2d214".ToBinary() }
    };

    UTXOTable UTXOTable;

    SHA256 SHA256 = SHA256.Create();


    public TokenBitcoin(string pathBlockArchive) 
      : base(pathBlockArchive)
    {
      UTXOTable = new UTXOTable(GetGenesisBlockBytes());
    }

    List<UTXOTable.TX> TXPool = new();

    internal void SendTX()
    {
      //UTXOTable.TX tXAnchorToken =
      //  UTXOTable.Wallet.CreateAnchorToken(
      //  "BB66AA55AA55AA55AA55AA55AA55AA55AA55AA55AA55EE11EE11EE11EE11EE11EE11EE11EE11EE11".ToBinary());

      //TXPool.Add(tXAnchorToken);

      //Network.AdvertizeToken(tXAnchorToken.Hash);
    }



    public override void StartMiner()
    {
      HeaderBitcoin header = new();
      HeaderBitcoin headerBitcoinTip = null;
      BlockBitcoin block = new(header);

      do
      {
        if (FlagMinerStop)
        {
          return;
        }

        header.Nonce += 1;

        if(header.Nonce == 0)
        {
          header.UnixTimeSeconds =
            (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        if (headerBitcoinTip != Blockchain.HeaderTip)
        {
          headerBitcoinTip = (HeaderBitcoin)Blockchain.HeaderTip;

          header.Height = headerBitcoinTip.Height + 1;

          header.Version = headerBitcoinTip.Version;

          headerBitcoinTip.Hash.CopyTo(header.HashPrevious, 0);

          header.UnixTimeSeconds =
            (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

          header.NBits = GetNextTarget(headerBitcoinTip);
          header.ComputeDifficultyFromNBits();
        }

        header.Hash =
          SHA256.ComputeHash(
            SHA256.ComputeHash(
              header.GetBytes()));

      } while (header.Hash.IsGreaterThan(header.NBits));

      while (!Blockchain.TryLock())
      {
        Thread.Sleep(100);
      }

      try
      {
        Blockchain.InsertBlock(block);
      }
      catch (Exception ex)
      {
        Debug.WriteLine(
          $"{ex.GetType().Name} when inserting " +
          $"mined block {block.Header.Hash.ToHexString()}.");
      }
      finally
      {
        Blockchain.ReleaseLock();
      }

      Network.RelayBlock(block);
    }


    public override Block CreateBlock()
    {
      return new BlockBitcoin();
    }

    public override Block CreateBlock(int sizeBuffer)
    {
      return new BlockBitcoin(sizeBuffer);
    }

    public override string GetName()
    {
      return GetType().Name;
    }

    public override bool TryRequestTX(
      byte[] hash, 
      out byte[] tXRaw)
    {
      UTXOTable.TX tX = TXPool.Find(t => t.Hash.IsEqual(hash));

      if(tX == null)
      {
        tXRaw = null;
        return false;
      }

      tXRaw = tX.TXRaw;
      return true;
    }



    public override Header CreateHeaderGenesis()
    {
      return new HeaderBitcoin(
         headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
         version: 0x01,
         hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
         merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
         unixTimeSeconds: 1231006505,
         nBits: 0x1d00ffff,
         nonce: 2083236893);
    }

    public override Dictionary<int, byte[]> GetCheckpoints()
    {
      return Checkpoints;
    }

    public override void LoadImage(string pathImage)
    {
      UTXOTable.LoadImage(pathImage);
    }

    public override void CreateImage(string pathImage)
    {
      UTXOTable.CreateImage(pathImage);
    }

    public override void Reset()
    {
      UTXOTable.Clear();
    }

    public override void InsertBlock(
      Block block,
      int indexBlockArchive)
    {
      ValidateHeader(block.Header);

      UTXOTable.InsertBlock(
        ((BlockBitcoin)block).TXs,
        indexBlockArchive);
    }

    public override string GetStatus()
    {
      return
        Blockchain.GetStatus() +
        UTXOTable.GetStatus();
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

    public override Header ParseHeader(
        byte[] buffer,
        ref int index,
        SHA256 sHA256)
    {
      return BlockBitcoin.ParseHeader(
        buffer, 
        ref index,
        sHA256);
    }

    public override void ValidateHeader(
      Header headerBlockchain)
    {
      HeaderBitcoin header = (HeaderBitcoin)headerBlockchain;

      if (Checkpoints
        .TryGetValue(header.Height, out byte[] hashCheckpoint) &&
        !hashCheckpoint.IsEqual(header.Hash))
      {
        throw new ProtocolException(
          string.Format(
            "Header {0} at hight {1} not equal to checkpoint hash {2}",
            header.Hash.ToHexString(),
            header.Height,
            hashCheckpoint.ToHexString()));
      }

      uint medianTimePast = GetMedianTimePast(
      (HeaderBitcoin)header.HeaderPrevious);

      if (header.UnixTimeSeconds < medianTimePast)
      {
        throw new ProtocolException(
          string.Format(
            "Header {0} with unix time {1} " +
            "is older than median time past {2}.",
            header.Hash.ToHexString(),
            DateTimeOffset.FromUnixTimeSeconds(header.UnixTimeSeconds),
            DateTimeOffset.FromUnixTimeSeconds(medianTimePast)));
      }

      uint targetBitsNew = GetNextTarget(
        (HeaderBitcoin)header.HeaderPrevious);

      if (header.NBits != targetBitsNew)
      {
        throw new ProtocolException(
          string.Format(
            "In header {0}\n nBits {1} not equal to target nBits {2}",
            header.Hash.ToHexString(),
            header.NBits,
            targetBitsNew));
      }
    }


    static uint GetMedianTimePast(HeaderBitcoin header)
    {
      const int MEDIAN_TIME_PAST = 11;

      List<uint> timestampsPast = new();

      int depth = 0;
      while (depth < MEDIAN_TIME_PAST)
      {
        timestampsPast.Add(header.UnixTimeSeconds);

        if (header.HeaderPrevious == null)
        { break; }

        header = (HeaderBitcoin)header.HeaderPrevious;
        depth++;
      }

      timestampsPast.Sort();

      return timestampsPast[timestampsPast.Count / 2];
    }

    const int RETARGETING_BLOCK_INTERVAL = 2016;
    const ulong RETARGETING_TIMESPAN_INTERVAL_SECONDS = 14 * 24 * 60 * 60;

    static readonly UInt256 DIFFICULTY_1_TARGET =
      new UInt256("00000000FFFF0000000000000000000000000000000000000000000000000000".ToBinary());


    static uint GetNextTarget(HeaderBitcoin header)
    {
      if((header.Height % RETARGETING_BLOCK_INTERVAL) != 0)
        return header.NBits;

      HeaderBitcoin headerIntervalStart = header;
      int depth = RETARGETING_BLOCK_INTERVAL;

      while (
        --depth > 0 && 
        headerIntervalStart.HeaderPrevious != null)
      {
        headerIntervalStart = 
          (HeaderBitcoin)headerIntervalStart.HeaderPrevious;
      }

      ulong actualTimespan = Limit(
        header.UnixTimeSeconds -
        headerIntervalStart.UnixTimeSeconds);

      UInt256 targetOld = UInt256.ParseFromCompact(header.NBits);

      UInt256 targetNew = targetOld
        .MultiplyBy(actualTimespan)
        .DivideBy(RETARGETING_TIMESPAN_INTERVAL_SECONDS);

      return UInt256.Min(DIFFICULTY_1_TARGET, targetNew)
        .GetCompact();
    }

    static ulong Limit(ulong actualTimespan)
    {
      if (actualTimespan < RETARGETING_TIMESPAN_INTERVAL_SECONDS / 4)
      {
        return RETARGETING_TIMESPAN_INTERVAL_SECONDS / 4;
      }

      if (actualTimespan > RETARGETING_TIMESPAN_INTERVAL_SECONDS * 4)
      {
        return RETARGETING_TIMESPAN_INTERVAL_SECONDS * 4;
      }

      return actualTimespan;
    }
  }
}
