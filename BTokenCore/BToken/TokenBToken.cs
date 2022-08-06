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
  partial class TokenBToken : Token
  {
    DatabaseAccounts DatabaseAccounts;

    const int SIZE_BUFFER_BLOCK = 0x400000;

    List<byte[]> TrailHashesAnchor = new();
    int IndexTrail;

    const int LENGTH_DATA_ANCHOR_TOKEN = 66;
    const int LENGTH_DATA_P2PKH_INPUT = 180;
    const int LENGTH_DATA_TX_SCAFFOLD = 10;
    const int LENGTH_DATA_P2PKH_OUTPUT = 34;

    readonly byte[] ID_BTOKEN = { 0x01, 0x00 };

    const long COUNT_SATOSHIS_PER_DAY_MINING = 500000;
    const long TIMESPAN_DAY_SECONDS = 24 * 3600;

    const UInt16 COMPORT_BTOKEN = 8777;

    StreamWriter LogFile;

    string PathBlocksMinedUnconfirmed;


    public TokenBToken(Token tokenParent) 
      : base(
          COMPORT_BTOKEN,
          flagEnableInboundConnections: true)
    {
      TokenParent = tokenParent;
      tokenParent.AddTokenListening(this);

      DatabaseAccounts = new();

      LogFile = new StreamWriter(
        Path.Combine(GetName(), "LogToken"),
        false);

      PathBlocksMinedUnconfirmed = Path.Combine(
        GetName(),
        "BlocksMinedUnconfirmed");

      Directory.CreateDirectory(PathBlocksMinedUnconfirmed);

      foreach (string pathFile in Directory.GetFiles(PathBlocksMinedUnconfirmed))
      {
        BlockBToken block = new();
        block.Parse(File.ReadAllBytes(pathFile));
        BlocksMined.Add(block);
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

    public override void CreateImageDatabase(string pathImage)
    {
      DatabaseAccounts.CreateImage(pathImage);
    }


    List<BlockBToken> BlocksMined = new();

    public override void StartMining()
    {
      if (IsMining)
        return;

      IsMining = true;

      RunMining();

      "Started miner".Log(LogFile);
    }


    SHA256 SHA256Miner = SHA256.Create();
    Random RandomGeneratorMiner = new();

    const int TIMESPAN_MINING_LOOP_MILLISECONDS = 1000;

    double FeePerByte;
    const double FACTOR_INCREMENT_FEE_PER_BYTE = 5.0;




    async Task RunMining()
    {
      FeePerByte = TokenParent.FeePerByteAverage;

      $"Miners starts with fee per byte = {FeePerByte}".Log(LogFile);

      while (IsMining)
      {
        int timeMSLoop = TIMESPAN_MINING_LOOP_MILLISECONDS;

        if (TryLock())
        {
          if(TryMineAnchorToken(out TokenAnchor tokenAnchor))
          {
            timeMSLoop = (int)(tokenAnchor.TX.Fee * TIMESPAN_DAY_SECONDS * 1000 /
                COUNT_SATOSHIS_PER_DAY_MINING);

            ($"Miner successfully mined {tokenAnchor.TX} with fee {tokenAnchor.TX.Fee}.\n" +
              $"Next attempt to create anchor token in {timeMSLoop / 60000} minutes.").Log(LogFile);
          }

          //timeMSLoop = RandomGeneratorMiner.Next(
          //  timeMSCreateNextAnchorToken / 2,
          //  timeMSCreateNextAnchorToken * 3 / 2);

          ReleaseLock();
        }

        await Task.Delay(timeMSLoop).ConfigureAwait(false);
      }
    }

    int NumberSequence;

    bool TryMineAnchorToken(out TokenAnchor tokenAnchor)
    {
      $"Miner tries to mine an anchor token".Log(LogFile);

      long feeAccrued = (long)FeePerByte * LENGTH_DATA_TX_SCAFFOLD;
      long feeAnchorToken = (long)FeePerByte * LENGTH_DATA_ANCHOR_TOKEN;
      long feePerInput = (long)FeePerByte * LENGTH_DATA_P2PKH_INPUT;
      long feeOutputChange = (long)FeePerByte * LENGTH_DATA_P2PKH_OUTPUT;

      long valueAccrued = 0;

      tokenAnchor = new()
      {
        NumberSequence = NumberSequence
      };

      while (
        tokenAnchor.TXOutputsWallet.Count < VarInt.PREFIX_UINT16 - 1 &&
        TokenParent.Wallet.TryGetOutputSpendable(
          feePerInput,
          out TXOutputWallet outputSpendable))
      {
        tokenAnchor.TXOutputsWallet.Add(outputSpendable);
        valueAccrued += outputSpendable.Value;
        feeAccrued += feePerInput;
      }

      feeAccrued += feeAnchorToken;

      if (valueAccrued < feeAccrued)
      {
        ($"Miner wallet has not enough value {valueAccrued} to " +
          $"pay for anchor token fee {feeAccrued}").Log(LogFile);

        return false;
      }

      BlockBToken block = new();

      block.Header.AppendToHeader(
        Blockchain.HeaderTip,
        SHA256Miner);

      LoadTXs(block, (long)(200 * 100e8));

      tokenAnchor.IDToken = ID_BTOKEN;
      tokenAnchor.HashBlockReferenced = block.Header.Hash;
      tokenAnchor.HashBlockPreviousReferenced = block.Header.HashPrevious;

      tokenAnchor.ValueChange = valueAccrued - feeAccrued - feeOutputChange;

      tokenAnchor.Serialize(TokenParent.Wallet, SHA256Miner);

      if (tokenAnchor.ValueChange > 0)
        TokenParent.Wallet.AddOutputSpendable(
          new TXOutputWallet
          {
            TXID = tokenAnchor.TX.Hash,
            TXIDShort = tokenAnchor.TX.TXIDShort,
            OutputIndex = 1,
            Value = tokenAnchor.ValueChange
          });
      
      $"Miner advertizes anchor token:\n {tokenAnchor.GetDescription()}"
        .Log(LogFile);

      string pathFileBlock = Path.Combine(
        PathBlocksMinedUnconfirmed, 
        block.ToString());

      File.WriteAllBytes(pathFileBlock, block.Buffer);

      lock (LOCK_BlocksMined)
        BlocksMined.Add(block);

      TokensAnchorMinedUnconfirmed.Add(tokenAnchor);

      // Immer bevor ein Token an einen Peer advertized wird,
      // fragt man den Peer ob er die Ancestor TX schon hat.
      // Wenn nicht iterativ weiterfragen und dann alle Tokens schicken.

      TokenParent.Network.AdvertizeTX(tokenAnchor.TX);

      $"{BlocksMined.Count} mined anchor tokens waiting for inclusion in next Bitcoin block."
        .Log(LogFile);

      return true;
    }


    List<TokenAnchor> TokensAnchorMinedUnconfirmed = new();
    List<TokenAnchor> TokensAnchorDetectedInBlock = new();

    public override void DetectAnchorTokenInBlock(TX tX)
    {
      TokenAnchor tokenAnchor = TokensAnchorMinedUnconfirmed
        .Find(t => t.TX.Hash.IsEqual(tX.Hash));

      if (tokenAnchor != null)
        TokensAnchorMinedUnconfirmed.Remove(tokenAnchor);
      else
      {
        TXOutput tXOutput = tX.TXOutputs[0];

        int index = tXOutput.StartIndexScript;

        if (tXOutput.Buffer[index] != 0x6A)
          return;

        index += 1;

        if (tXOutput.Buffer[index] != LENGTH_DATA_ANCHOR_TOKEN)
          return;

        index += 1;

        if (!ID_BTOKEN.IsEqual(tXOutput.Buffer, index))
          return;

        $"Detected anchor token {tX} in Bitcoin block, BToken block not yet in MemPool."
        .Log(LogFile);

        index += ID_BTOKEN.Length;

        tokenAnchor = new(tX, index);
      }

      tokenAnchor.IsConfirmed = true;
      TokensAnchorDetectedInBlock.Add(tokenAnchor);

      $"Anchor token detected.:\n {tokenAnchor.GetDescription()}"
        .Log(LogFile);
    }

    readonly object LOCK_BlocksMined = new();

    public override void SignalCompletionBlockInsertion(byte[] hashBlock)
    {
      if (TokensAnchorDetectedInBlock.Count == 0)
      {
        $"No anchor tokens detected in block {hashBlock.ToHexString()}.".Log(LogFile);
        return;
      }

      TokenAnchor tokenAnchorWinner = GetTXAnchorWinner(hashBlock);

      ($"The winning anchor token is {tokenAnchorWinner.TX} referencing block " +
        $"{tokenAnchorWinner.HashBlockReferenced.ToHexString()}.").Log(LogFile);

      TrailHashesAnchor.Add(tokenAnchorWinner.HashBlockReferenced);
      IndexTrail += 1;

      if (BlocksMined.Count == 0)
        return;

      BlockBToken blockMined = BlocksMined.Find(b => 
      b.Header.Hash.IsEqual(tokenAnchorWinner.HashBlockReferenced));

      if (blockMined != null)
      {
        $"The winning block is self mined.".Log(LogFile);
        InsertBlock(blockMined);
        Network.RelayBlockToNetwork(blockMined);
      }
    }

    TokenAnchor GetTXAnchorWinner(byte[] hashBlockAnchor)
    {
      SHA256 sHA256 = SHA256.Create();

      byte[] targetValue = sHA256.ComputeHash(hashBlockAnchor);
      byte[] biggestDifferenceTemp = new byte[32];
      TokenAnchor tokenAnchorWinner = null;

      TokensAnchorDetectedInBlock.ForEach(t =>
      {
        byte[] differenceHash = targetValue.SubtractByteWise(
          t.HashBlockReferenced);

        if (differenceHash.IsGreaterThan(biggestDifferenceTemp))
        {
          biggestDifferenceTemp = differenceHash;
          tokenAnchorWinner = t;
        }
      });

      TokensAnchorDetectedInBlock.Clear(); // warum das hier machen?

      return tokenAnchorWinner;
    }


    protected override void InsertInDatabase(
      Block block,
      Network.Peer peer)
    {
      $"Insert BToken block {block} in database.".Log(LogFile);

      int indexTrailAnchorPrevious = ((HeaderBToken)Blockchain.HeaderTip).IndexTrailAnchor;

      if (TrailHashesAnchor.Count - 1 == indexTrailAnchorPrevious)
      {
        $"The anchoring Bitcoin block has not yet been received.".Log(LogFile);
        throw new NotSynchronizedWithParentException();
      }

      if (!TrailHashesAnchor[indexTrailAnchorPrevious + 1].IsEqual(block.Header.Hash))
      {
        ($"The anchoring Bitcoin block does not anchor BToken block {block}\n" +
          $"but {TrailHashesAnchor[indexTrailAnchorPrevious + 1].ToHexString()}.").Log(LogFile);

        throw new NotSynchronizedWithParentException();
      }

      DatabaseAccounts.InsertBlock(block);

      ((HeaderBToken)block.Header).IndexTrailAnchor = indexTrailAnchorPrevious + 1;

      RBFAnchorTokens();
    }

    void RBFAnchorTokens()
    {
      ($"{BlocksMined.Count} blocks were mined, {TokensAnchorMinedUnconfirmed.Count}"
        + "tokens did not make it into bitcoin block.").Log(LogFile);

      if (BlocksMined.Count == 0)
        return;

      BlocksMined.Clear();

      if (TokensAnchorMinedUnconfirmed.Count == 0)
      {
        FeePerByte /= FACTOR_INCREMENT_FEE_PER_BYTE;
        NumberSequence = 0;
        $"New fee per byte is {FeePerByte}, sequence number {NumberSequence}.".Log(LogFile);
        $"All anchor tokens made it into Bitcoin block, nothing to RBF.".Log(LogFile);
        return;
      }

      FeePerByte *= FACTOR_INCREMENT_FEE_PER_BYTE;
      NumberSequence += 1;

      $"{TokensAnchorMinedUnconfirmed.Count} anchor tokens, do RBF.".Log(LogFile);
      $"New fee per byte is {FeePerByte}, sequence number {NumberSequence}.".Log(LogFile);

      TokensAnchorMinedUnconfirmed.Reverse();

      foreach (TokenAnchor tokenAnchorMinedUnconfirmed in TokensAnchorMinedUnconfirmed)
      {
        if (tokenAnchorMinedUnconfirmed.ValueChange > 0)
          Wallet.RemoveOutputSpendable(tokenAnchorMinedUnconfirmed.TX.Hash);

        tokenAnchorMinedUnconfirmed.TXOutputsWallet
          .ForEach(i => Wallet.AddOutputSpendable(i));
      }

      int countTokensAnchorMined = TokensAnchorMinedUnconfirmed.Count;

      TokensAnchorMinedUnconfirmed.Clear();

      foreach (string pathFile in Directory.GetFiles(PathBlocksMinedUnconfirmed))
        File.Delete(pathFile);

      if (TryMineAnchorToken(out TokenAnchor tokenAnchor))
        for (int j = 1; j < countTokensAnchorMined; j += 1)
          if (!TryMineAnchorToken(out tokenAnchor))
            break;
    }
    
    public override void RevokeBlockInsertion()
    {
      TokensAnchorDetectedInBlock.Clear();
    }

    void LoadTXs(Block block, long blockReward)
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

      tXRaw.AddRange(BitConverter.GetBytes(blockReward));

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

    public override HeaderDownload CreateHeaderDownload()
    {
      return new HeaderDownloadBToken(
        Blockchain.GetLocator(),
        TrailHashesAnchor,
        IndexTrail);
    }

    public override List<string> GetSeedAddresses()
    {
      return new List<string>()
      {
        "84.75.2.239", "83.229.86.158"
      };
    }

  }
}
