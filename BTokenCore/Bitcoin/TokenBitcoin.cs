﻿using System;
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
    UTXOTable UTXOTable;


    public TokenBitcoin(string pathBlockArchive)
      : base(pathBlockArchive)
    {
      Blockchain.Checkpoints.Add(
        250000, 
        "000000000000003887df1f29024b06fc2200b55f8af8f35453d7be294df2d214".ToBinary());

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

      int numberOfProcesses = Math.Max(Environment.ProcessorCount - 1, 1);
      long nonceSegment = uint.MaxValue / numberOfProcesses;

      Parallel.For(
        0,
        numberOfProcesses,
        i => StartMinerProcess(i * nonceSegment));

      IsMinerRunning = false;
      FlagMinerCancel = false;

      Console.WriteLine("Miner canceled.");
    }

    void StartMinerProcess(long nonceStart)
    {
      SHA256 sHA256 = SHA256.Create();

      try
      {
        while (true)
        {
          HeaderBitcoin header = new();
          BlockBitcoin block = new(header);

          ComputePoW(
            block,
            sHA256,
            nonceStart);

          block.Buffer = header.Buffer
            .Concat(VarInt.GetBytes(block.TXs.Count))
            .Concat(block.TXs[0].TXRaw).ToArray();

          header.CountBlockBytes = block.Buffer.Length;

          while (!Blockchain.TryLock())
          {
            if (FlagMinerCancel)
              return;

            Console.WriteLine("Miner awaiting access of Bitcoin blockchain LOCK.");
            Thread.Sleep(1000);
          }

          Console.Beep();

          try
          {
            Blockchain.InsertBlock(block);

            Debug.WriteLine($"Mined Bitcon block {block}.");
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
      }
      catch (TaskCanceledException)
      {
        return;
      }
    }

    void LoadTXs(BlockBitcoin block)
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

      UTXOTable.TX tX = block.ParseTX(
        true,
        tXRawArray,
        ref indexTXRaw);

      tX.TXRaw = tXRawArray;

      block.TXs.Add(tX);
      block.Header.MerkleRoot = tX.Hash;
    }

    void ComputePoW(
      BlockBitcoin block,
      SHA256 sHA256,
      long nonceStart)
    {
      HeaderBitcoin headerTip = null;
      HeaderBitcoin header = (HeaderBitcoin)block.Header;

      do
      {
        if (FlagMinerCancel)
          throw new TaskCanceledException();

        header.IncrementNonce();

        if (headerTip != Blockchain.HeaderTip)
        {
          headerTip = (HeaderBitcoin)Blockchain.HeaderTip;

          header.Height = headerTip.Height + 1;

          LoadTXs(block);

          header.Version = headerTip.Version;

          headerTip.Hash.CopyTo(header.HashPrevious, 0);

          header.UnixTimeSeconds =
            (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

          header.NBits = HeaderBitcoin.GetNextTarget(headerTip);
          header.ComputeDifficultyFromNBits();

          header.Nonce = (uint)nonceStart;

          header.Buffer = header.GetBytes();
        }
        else if (header.Nonce == 0)
        {
          header.UnixTimeSeconds =
            (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

          header.Nonce = (uint)nonceStart;

          header.Buffer = header.GetBytes();
        }

        header.Hash =
          sHA256.ComputeHash(
            sHA256.ComputeHash(header.Buffer));

      } while (header.Hash.IsGreaterThan(header.NBits));
    }

    public override Block CreateBlock()
    {
      return new BlockBitcoin();
    }

    public override Block CreateBlock(int sizeBuffer)
    {
      return new BlockBitcoin(sizeBuffer);
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
      HeaderBitcoin header = new(
         headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
         version: 0x01,
         hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
         merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
         unixTimeSeconds: 1231006505,
         nBits: 0x1d00ffff,
         nonce: 2083236893);

      header.Height = 0;
      header.DifficultyAccumulated = header.Difficulty;

      return header;
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

    protected override void InsertInDatabase(Block block)
    {
      UTXOTable.InsertBlock(
        block.TXs,
        block.Header.IndexBlockArchive);
    }

    public override string GetStatus()
    {
      return
        Blockchain.GetStatus();
        //+ UTXOTable.GetStatus();
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
  }
}
