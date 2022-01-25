using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security.Cryptography;


using BTokenLib;

namespace BTokenCore
{
  class TokenBToken : Token
  {
    public TokenBToken(
      string pathBlockArchive,
      Token tokenParent,
      Token tokenChild) 
      : base(pathBlockArchive, tokenParent)
    {
      TokenParent = tokenParent;
    }

    public override Header CreateHeaderGenesis()
    {
      HeaderBToken header = new(
         headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
         hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
         hashAnchorPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
         merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
         unixTimeSeconds: 1231006505);

      header.Height = 0;
      header.DifficultyAccumulated = header.Difficulty;

      return header;
    }

    bool IsMinerRunning;
    bool FlagMinerCancel;

    public override void StopMiner()
    {
      if (IsMinerRunning)
        FlagMinerCancel = true;
    }

    public override void StartMiner()
    {
      if (IsMinerRunning)
        return;

      IsMinerRunning = true;

      StartMinerProcess();
    }

    async Task StartMinerProcess()
    {
      SHA256 sHA256 = SHA256.Create();

      while (!FlagMinerCancel)
      {
        BlockBToken block = await MineBlock(sHA256);

        while (!Blockchain.TryLock())
        {
          if (FlagMinerCancel)
            goto LABEL_ExitMinerProcess;

          Console.WriteLine("Miner awaiting access of BToken blockchain LOCK.");
          Thread.Sleep(1000);
        }

        Console.Beep();

        try
        {
          Blockchain.InsertBlock(block);

          Debug.WriteLine($"Mined block {block}.");
        }
        catch (Exception ex)
        {
          Debug.WriteLine(
            $"{ex.GetType().Name} when inserting mined block {block}.");

          continue;
        }
        finally
        {
          Blockchain.ReleaseLock();
        }

        Network.RelayBlock(block);
      }

    LABEL_ExitMinerProcess:

      Console.WriteLine("Miner canceled.");
    }

    async Task<BlockBToken> MineBlock(SHA256 sHA256)
    {
      HeaderBToken headerBToken = new();
      BlockBToken blockBToken = new(headerBToken);

      HeaderBToken headerBTokenTip = (HeaderBToken)Blockchain.HeaderTip;
      
      Block blockAnchor;
      byte[] tXAnchorHash;

      do
      {
        if (FlagMinerCancel)
          throw new TaskCanceledException();

        headerBToken.Height = headerBTokenTip.Height + 1;

        LoadTXs(blockBToken);

        headerBTokenTip.Hash.CopyTo(headerBToken.HashPrevious, 0);

        headerBToken.UnixTimeSeconds =
          (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        headerBToken.Buffer = headerBToken.GetBytes();

        headerBToken.Hash =
          sHA256.ComputeHash(
            sHA256.ComputeHash(headerBToken.Buffer));

        tXAnchorHash = TokenAnchor.SendDataTX("BToken" + headerBToken.Hash);

        blockAnchor = await TokenAnchor.AwaitNextBlock();

      } while (!IsHashTXAnchorWinner(
        blockAnchor,
        tXAnchorHash,
        sHA256));

      blockBToken.Buffer = headerBToken.Buffer
        .Concat(VarInt.GetBytes(blockBToken.TXs.Count))
        .Concat(blockBToken.TXs[0].TXRaw).ToArray();

      headerBToken.CountBlockBytes = blockBToken.Buffer.Length;

      return blockBToken;
    }

    bool IsHashTXAnchorWinner(
      Block blockAnchor, 
      byte[] hashTXAnchor,
      SHA256 sHA256)
    {
      byte[] hashBlockAnchor = sHA256.ComputeHash(blockAnchor.Header.Hash);
      byte[] hashTXWinner = null;
      byte[] largestDifference = new byte[32];

      blockAnchor.TXs.ForEach( tX =>
      {
        byte[] difference = hashBlockAnchor.SubtractByteWise(tX.Hash);

        if (difference.IsGreaterThan(largestDifference))
        {
          largestDifference = difference;
          hashTXWinner = tX.Hash;
        }
      });

      return hashTXWinner.IsEqual(hashTXAnchor);
    }

    void LoadTXs(BlockBToken block)
    {
      List<byte> tXRaw = new();

      tXRaw.AddRange(new byte[4] { 0x01, 0x00, 0x00, 0x00 }); // version

      tXRaw.Add(0x01); // #TxIn

      tXRaw.AddRange(new byte[32]); // TxOutHash

      tXRaw.AddRange("FFFFFFFF".ToBinary()); // TxOutIndex

      List<byte> blockHeight = VarInt.GetBytes(block.Header.Height); // Script coinbase
      tXRaw.Add((byte)blockHeight.Count);
      tXRaw.AddRange(blockHeight);

      tXRaw.AddRange("FFFFFFFF".ToBinary()); // sequence

      tXRaw.Add(0x01); // #TxOut

      ulong valueChange = (ulong)(50000 * 100e8);
      tXRaw.AddRange(BitConverter.GetBytes(valueChange));

      tXRaw.AddRange(UTXOTable.GetReceptionScript());

      tXRaw.AddRange(new byte[4]);


      int indexTXRaw = 0;
      byte[] tXRawArray = tXRaw.ToArray();

      TX tX = block.ParseTX(
        true,
        tXRawArray,
        ref indexTXRaw);

      tX.TXRaw = tXRawArray;

      block.TXs = new List<TX>() { tX };
      block.Header.MerkleRoot = tX.Hash;
    }

    public override void CreateImage(string pathImage)
    {
      throw new NotImplementedException();
    }

    public override void LoadImage(string pathImage)
    {
      throw new NotImplementedException();
    }

    public Dictionary<int, byte[]> GetCheckpoints()
    {
      return new Dictionary<int, byte[]>();
    }

    public override Header ParseHeader(
        byte[] buffer,
        ref int index)
    {
      BlockBToken bTokenBlock = new();

      return bTokenBlock.ParseHeader(
        buffer,
        ref index);
    }

    public override Block CreateBlock()
    {
      return new BlockBToken();
    }

    public override Block CreateBlock(int sizeBuffer)
    {
      return new BlockBToken(sizeBuffer);
    }

    public override string GetStatus()
    {
      return
        Blockchain.GetStatus();
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
      throw new NotImplementedException();
    }


    public override bool TryRequestTX(
      byte[] hash,
      out byte[] tXRaw)
    {
      throw new NotImplementedException();
    }
  }
}
