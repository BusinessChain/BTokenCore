﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Security.Cryptography;


using BTokenLib;

namespace BTokenCore
{
  class TokenBToken : Token
  {
    DatabaseAccounts DatabaseAccounts;

    BlockArchiver Archiver;

    const int SIZE_BUFFER_BLOCK = 0x400000;

    List<byte[]> TrailHashesAnchor = new();
    int IndexTrail;
    long TimeUpdatedTrailUnixTimeSeconds;

    const int LENGTH_DATA_ANCHOR_TOKEN = 77;
    const int LENGTH_DATA_P2PKH_INPUT = 180;
    const int LENGTH_DATA_TX_SCAFFOLD = 10;
    const int LENGTH_DATA_P2PKH_OUTPUT = 34;

    readonly byte[] ID_BTOKEN = { 0x01, 0x00 };

    const long COUNT_SATOSHIS_PER_DAY_MINING = 4850;
    const long TIMESPAN_DAY_SECONDS = 24 * 3600;


    public TokenBToken(Token tokenParent)
      : base()
    {
      TokenParent = tokenParent;
      tokenParent.AddTokenListening(this);

      DatabaseAccounts = new();

      Archiver = new(this, GetName());
    }

    public override Header CreateHeaderGenesis()
    {
      HeaderBToken header = new(
        headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
        hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
        merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
        unixTimeSeconds: 1231006505,
        nonce: 0);

      header.Height = 0;
      header.DifficultyAccumulated = header.Difficulty;

      return header;
    }


    public override void LoadImageDatabase(string pathImage)
    {
      DatabaseAccounts.LoadImage(pathImage);

      byte[] bytesBlockTrail = File.ReadAllBytes(
        Path.Combine(pathImage, "ImageBlockTrail"));

      for (int i = 0; i * 32 < bytesBlockTrail.Length; i += 1)
      {
        TrailHashesAnchor[i] = new byte[32];
        Array.Copy(bytesBlockTrail, i * 32, TrailHashesAnchor[i], 0, 32);

        IndexTrail += 1;
      }
    }


    List<Block> BlocksMined = new();
    long FeeMinerDisposable = COUNT_SATOSHIS_PER_DAY_MINING;

    public override void StartMining()
    {
      if (IsMining)
        return;

      IsMining = true;

      RunMining();
    }


    const int TIMESPAN_MINING_LOOP_SECONDS = 5;


    SHA256 SHA256Miner = SHA256.Create();

    async Task RunMining()
    {
      int nonce = 0;

      while (!FlagMiningCancel)
      {
        BlockBToken block = new(
          new HeaderBToken() { Nonce = nonce++ });

        //LoadTXs(block); // trägt referenzen auf TXs im pool, so kann ohne memory leak einfach immer ein neuer Block gmacht werden.

        block.Header.AppendToHeader(
          Blockchain.HeaderTip,
          SHA256Miner);

        if (TryCreateAnchorToken(block, out TokenAnchor tokenAnchor))
        {
          BlocksMined.Add(block);

          FeeMinerDisposable -= tokenAnchor.Fee;

          TokenParent.Network.AdvertizeTX(tokenAnchor);

          int frequencyTimeAnchorToken = 
            (int)(tokenAnchor.Fee * TIMESPAN_DAY_SECONDS /
            COUNT_SATOSHIS_PER_DAY_MINING);

          await Task.Delay(frequencyTimeAnchorToken * 2 * 1000) // double span for average middle
            .ConfigureAwait(false);
        }
        else
        {
          FeeMinerDisposable += COUNT_SATOSHIS_PER_DAY_MINING *
            TIMESPAN_MINING_LOOP_SECONDS / TIMESPAN_DAY_SECONDS;

          await Task.Delay(TIMESPAN_MINING_LOOP_SECONDS * 1000)
            .ConfigureAwait(false);
        }
      }
    }


    public bool TryCreateAnchorToken( 
      Block block, 
      out TokenAnchor tokenAnchor)
    {
      long feeAccrued = TokenParent.FeePerByte * LENGTH_DATA_TX_SCAFFOLD;
      long feeAnchorToken = TokenParent.FeePerByte * LENGTH_DATA_ANCHOR_TOKEN;
      long feePerInput = TokenParent.FeePerByte * LENGTH_DATA_P2PKH_INPUT;
      long feeOutputChange = TokenParent.FeePerByte * LENGTH_DATA_P2PKH_OUTPUT;

      long valueAccrued = 0;

      tokenAnchor = new();

      foreach (TXOutputWallet outputSpendable in 
        TokenParent.Wallet.GetOutputsSpendableSortedValueDescending())
      {
        if (
          outputSpendable.Value <= feePerInput ||
          tokenAnchor.Inputs.Count == VarInt.PREFIX_UINT16 - 1)
          break;

        tokenAnchor.Inputs.Add(outputSpendable);
        valueAccrued += outputSpendable.Value;
        feeAccrued += feePerInput;
      }

      feeAccrued += feeAnchorToken;

      if (FeeMinerDisposable < feeAccrued || valueAccrued < feeAccrued)
        return false;

      tokenAnchor.DataAnchorToken = ID_BTOKEN
        .Concat(block.Header.Hash)
        .Concat(block.Header.HashPrevious).ToArray();

      tokenAnchor.ValueChange = valueAccrued - feeAccrued - feeOutputChange;

      if (tokenAnchor.ValueChange > feePerInput / 4)
        feeAccrued += feeOutputChange;

      tokenAnchor.Fee = feeAccrued;

      tokenAnchor.Serialize(TokenParent.Wallet, SHA256);

      return true;
    }

    public override HeaderDownload CreateHeaderDownload()
    {
      return new HeaderDownloadBToken(
        Blockchain.GetLocator(),
        TrailHashesAnchor,
        IndexTrail);
    }

    protected override void InsertInDatabase(
      Block block, 
      Network.Peer peer)
    {
      if(IndexTrail == TrailHashesAnchor.Count)
      {
        Block blockParent = peer.GetBlock(
          ((HeaderBToken)block.Header).HashHeaderAnchor);

        TokenParent.InsertBlock(blockParent, peer);
      }

      if (
        IndexTrail == TrailHashesAnchor.Count ||
        !block.Header.Hash.IsEqual(TrailHashesAnchor[IndexTrail]))
        throw new ProtocolException(
          $"Header hash {block} not equal to anchor " +
          $"trail hash {TrailHashesAnchor[IndexTrail].ToHexString()}.");

      DatabaseAccounts.InsertBlock(block);
      Archiver.ArchiveBlock(block);
    }


    List<TokenAnchor> TokensAnchor = new();

    public override void DetectAnchorToken(TXOutput tXOutput)
    {
      int index = tXOutput.StartIndexScript;

      if (tXOutput.Buffer[index] != 0x6A)
        return;

      index += 1;

      byte lengthData = tXOutput.Buffer[index];

      if (lengthData != LENGTH_DATA_ANCHOR_TOKEN)
        return;

      index += 1;

      if (ID_BTOKEN.IsEqual(tXOutput.Buffer, index))
        return;

      index += 2;

      if (!TokensAnchor.Any(t => t.HashBlock.IsEqual(tXOutput.Buffer, index)))
        TokensAnchor.Add(new TokenAnchor(tXOutput.Buffer, index));
    }

    public override void RevokeBlockInsertion()
    {
      TokensAnchor.Clear();
    }

    SHA256 SHA256 = SHA256.Create();

    TokenAnchor GetTXAnchorWinner(byte[] hashBlockAnchor)
    {
      byte[] targetValue = SHA256.ComputeHash(hashBlockAnchor);
      byte[] biggestDifferenceTemp = new byte[32];
      TokenAnchor tokenAnchorWinner = null;

      TokensAnchor.ForEach(t =>
      {
        byte[] differenceHash = targetValue.SubtractByteWise(
          t.HashBlock);

        if (differenceHash.IsGreaterThan(biggestDifferenceTemp))
        {
          biggestDifferenceTemp = differenceHash;
          tokenAnchorWinner = t;
        }
      });

      TokensAnchor.Clear();

      return tokenAnchorWinner;
    }

    public override void SignalCompletionBlockInsertion(byte[] hashBlock)
    {
      if (TokensAnchor.Count == 0)
        return;
      
      TokenAnchor tokenAnchorWinner = GetTXAnchorWinner(hashBlock);

      TrailHashesAnchor.Add(tokenAnchorWinner.HashBlock);

      if(TryGetBlockFromMiner(tokenAnchorWinner, out Block blockMined))
      {
        InsertBlock(blockMined);
        Network.RelayBlock(blockMined);
      }
      else
        TimeUpdatedTrailUnixTimeSeconds = 
          DateTimeOffset.Now.ToUnixTimeSeconds();
    }


    readonly object LOCK_HeadersMined = new();
    readonly object LOCK_BlocksMined = new();

    bool TryGetBlockFromMiner(
      TokenAnchor tokenAnchor, 
      out Block blockMined)
    {
      blockMined = null;
      List<Header> headersMinedPurge = new();

      lock (LOCK_HeadersMined)
      {
        foreach(Block blockMined in BlocksMined)
        {
          if(blockMined.Header.Hash.IsEqual(tokenAnchor.HashBlock))
            blockMined = BlocksMined.Find(
              b => b.HashMerkleRoot.IsEqual(header.MerkleRoot));

          if (header.HashPrevious.IsEqual(tokenAnchor.HashPrevious))
          {
            headersMinedPurge.Add(header);
            BlocksMined.RemoveAll(b => b.HashMerkleRoot.IsEqual(
              header.MerkleRoot));
          }
        }

        headersMinedPurge.ForEach(h => HeadersMined.Remove(h));
      }

      return blockMined != null;
    }

    public void LoadTXs(Block block)
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

      tXRaw.AddRange(Wallet.GetReceptionScript());

      tXRaw.AddRange(new byte[4]);


      int indexTXRaw = 0;

      TX tX = block.ParseTX(
        true,
        tXRaw.ToArray(),
        ref indexTXRaw);

      tX.TXRaw = tXRaw;

      block.TXs = new List<TX>() { tX };
      block.Header.MerkleRoot = tX.Hash;
    }

    public override void CreateImageDatabase(string pathImage)
    {
      DatabaseAccounts.CreateImage(pathImage);
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

    public override BlockBToken CreateBlock()
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
      DatabaseAccounts.Reset();
      TrailHashesAnchor.Clear();
      IndexTrail = 0;
    }

    public override bool TryRequestTX(
      byte[] hash,
      out byte[] tXRaw)
    {
      throw new NotImplementedException();
    }


    public override List<string> GetSeedAddresses()
    {
      return new List<string>();
    }
  }
}
