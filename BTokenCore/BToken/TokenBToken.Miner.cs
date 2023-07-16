using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;

using BTokenLib;
using System.Diagnostics;

namespace BTokenCore
{
  partial class TokenBToken : Token
  {
    const int COUNT_TXS_PER_BLOCK_MAX = 5;
    const int TIMESPAN_MINING_ANCHOR_TOKENS_SECONDS = 10;
    const int TIME_MINER_PAUSE_AFTER_RECEIVE_PARENT_BLOCK_SECONDS = 10;
    const double FACTOR_INCREMENT_FEE_PER_BYTE = 1.2;

    const int LENGTH_DATA_ANCHOR_TOKEN = 66;
    const int LENGTH_DATA_P2PKH_INPUT = 180;
    const int LENGTH_DATA_TX_SCAFFOLD = 10;
    const int LENGTH_DATA_P2PKH_OUTPUT = 34;

    readonly byte[] ID_BTOKEN = { 0x01, 0x00 };

    string PathBlocksMinedUnconfirmed;
    List<BlockBToken> BlocksMined = new();

    SHA256 SHA256Miner = SHA256.Create();
    Random RandomGeneratorMiner = new();
    const double FEE_SATOSHI_PER_BYTE_INITIAL = 1.0;
    double FeeSatoshiPerByte;
    int NumberSequence;
    List<TokenAnchor> TokensAnchorUnconfirmed = new();

    List<TokenAnchor> TokensAnchorDetectedInBlock = new();



    public override void StartMining()
    {
      if (IsMining)
        return;

      IsMining = true;

      "Start BToken miner".Log(LogFile);

      RunMining();
    }

    async Task RunMining()
    {
      FeeSatoshiPerByte = FEE_SATOSHI_PER_BYTE_INITIAL; // TokenParent.FeePerByteAverage;

      $"Miners starts with fee per byte = {FeeSatoshiPerByte}".Log(LogFile);

      Header headerTipParent = null;
      Header headerTip = null;

      int timerMinerPause = 0;
      int timerCreateNextToken = 0;
      int timeMinerLoopMilliseconds = 100;

      while (IsMining)
      {
        if (TryLock())
        {
          if (headerTip == null)
          {
            headerTip = HeaderTip;
            headerTipParent = TokenParent.HeaderTip;
          }

          if(headerTipParent != TokenParent.HeaderTip)
          {
            timerMinerPause = TIME_MINER_PAUSE_AFTER_RECEIVE_PARENT_BLOCK_SECONDS * 1000 
              / timeMinerLoopMilliseconds;

            headerTipParent = TokenParent.HeaderTip;
          }

          if(headerTip != HeaderTip)
          {
            if (TokensAnchorUnconfirmed.Count == 0)
            {
              if (BlocksMined.Count > 0)
                FeeSatoshiPerByte /= FACTOR_INCREMENT_FEE_PER_BYTE;

              NumberSequence = 0;

              BlocksMined.Clear();
            }
            else
            {
              FeeSatoshiPerByte *= FACTOR_INCREMENT_FEE_PER_BYTE;
              NumberSequence += 1;

              RBFAnchorTokens();
            }

            headerTip = HeaderTip;
            timerMinerPause = 0;
          }

          if (timerMinerPause > 0)
            timerMinerPause -= 1;

          if (timerCreateNextToken > 0)
            timerCreateNextToken -= 1;

          if (timerMinerPause == 0 && timerCreateNextToken == 0)
            if (TryMineAnchorToken(out TokenAnchor tokenAnchor))
            {
              timerCreateNextToken = TIMESPAN_MINING_ANCHOR_TOKENS_SECONDS * 1000 
                / timeMinerLoopMilliseconds;

              TokensAnchorUnconfirmed.Add(tokenAnchor);

              TokenParent.BroadcastTX(tokenAnchor.TX);

              $"{TokensAnchorUnconfirmed.Count} mined unconfirmed anchor tokens referencing block {tokenAnchor.HashBlockReferenced.ToHexString()}.".Log(LogFile);

              // timeMSLoop = (int)(tokenAnchor.TX.Fee * TIMESPAN_DAY_SECONDS * 1000 /
              // COUNT_SATOSHIS_PER_DAY_MINING);

              // timeMSLoop = RandomGeneratorMiner.Next(
              // timeMSCreateNextAnchorToken / 2,
              // timeMSCreateNextAnchorToken * 3 / 2);
            }
        }

        ReleaseLock();
        await Task.Delay(timeMinerLoopMilliseconds).ConfigureAwait(false);
      }

      $"Exit BToken miner.".Log(LogFile);
    }

    void RBFAnchorTokens()
    {
      TokensAnchorUnconfirmed.Reverse();

      foreach (TokenAnchor t in TokensAnchorUnconfirmed)
      {
        if (t.ValueChange > 0)
          TokenParent.Wallet.RemoveOutput(t.TX.Hash);

        t.Inputs.ForEach(i => TokenParent.Wallet.AddOutput(i));

        File.Delete(Path.Combine(
          PathBlocksMinedUnconfirmed,
          t.HashBlockReferenced.ToHexString()));
      }

      int countTokensAnchorUnconfirmed = TokensAnchorUnconfirmed.Count;
      TokensAnchorUnconfirmed.Clear();

      $"RBF {countTokensAnchorUnconfirmed} anchorTokens".Log(LogFile);

      while (countTokensAnchorUnconfirmed-- > 0)
        if (TryMineAnchorToken(out TokenAnchor tokenAnchor))
          TokensAnchorUnconfirmed.Add(tokenAnchor);
        else
          break;

      TokenParent.BroadcastTX(TokensAnchorUnconfirmed.Select(t => t.TX).ToList());

      $"{TokensAnchorUnconfirmed.Count} RBF'ed unconfirmed anchor tokens.".Log(LogFile);
    }

    bool TryMineAnchorToken(out TokenAnchor tokenAnchor)
    {
      long feeAccrued = (long)(FeeSatoshiPerByte * LENGTH_DATA_TX_SCAFFOLD);
      long feeAnchorToken = (long)(FeeSatoshiPerByte * LENGTH_DATA_ANCHOR_TOKEN);
      long feePerInput = (long)(FeeSatoshiPerByte * LENGTH_DATA_P2PKH_INPUT);
      long feeOutputChange = (long)(FeeSatoshiPerByte * LENGTH_DATA_P2PKH_OUTPUT);

      long valueAccrued = 0;

      tokenAnchor = new();
      tokenAnchor.NumberSequence = NumberSequence;
      tokenAnchor.IDToken = ID_BTOKEN;

      while (
        tokenAnchor.Inputs.Count < VarInt.PREFIX_UINT16 - 1 &&
        TokenParent.Wallet.TryGetOutput(
          feePerInput,
          out TXOutputWallet output))
      {
        tokenAnchor.Inputs.Add(output);
        valueAccrued += output.Value;

        feeAccrued += feePerInput;
      }

      feeAccrued += feeAnchorToken;

      if (valueAccrued < feeAccrued)
        return false;

      BlockBToken block = new();

      int height = HeaderTip.Height + 1;

      long blockReward = BLOCK_REWARD_INITIAL >>
        height / PERIOD_HALVENING_BLOCK_REWARD;

      TX tXCoinbase = CreateCoinbaseTX(block, height, blockReward);

      block.TXs.Add(tXCoinbase);
      block.TXs.AddRange(
        TXPool.GetTXs(out int countTXsPool, COUNT_TXS_PER_BLOCK_MAX));

      HeaderBToken header = new()
      {
        HashPrevious = HeaderTip.Hash,
        HeaderPrevious = HeaderTip,
        Height = height,
        UnixTimeSeconds = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
        MerkleRoot = block.ComputeMerkleRoot()
      };

      block.Header = header;

      header.ComputeHash(SHA256Miner);

      block.Buffer = block.Header.Buffer.Concat(
        VarInt.GetBytes(block.TXs.Count)).ToArray();

      block.TXs.ForEach(t => 
      { block.Buffer = block.Buffer.Concat(t.TXRaw).ToArray(); });

      block.Header.CountBytesBlock = block.Buffer.Length;

      tokenAnchor.HashBlockReferenced = block.Header.Hash;
      tokenAnchor.HashBlockPreviousReferenced = block.Header.HashPrevious;
      tokenAnchor.ValueChange = valueAccrued - feeAccrued - feeOutputChange;
      tokenAnchor.Serialize(TokenParent, SHA256Miner);

      if (tokenAnchor.ValueChange > 0)
        TokenParent.Wallet.AddOutput(
          new TXOutputWallet
          {
            TXID = tokenAnchor.TX.Hash,
            TXIDShort = tokenAnchor.TX.TXIDShort,
            Index = 1,
            Value = tokenAnchor.ValueChange
          });
      
      string pathFileBlock = Path.Combine(
        PathBlocksMinedUnconfirmed, 
        block.ToString());

      // File.WriteAllBytes(pathFileBlock, block.Buffer);

      BlocksMined.Add(block);

      $"BToken miner successfully mined anchor Token {tokenAnchor.TX} with fee {tokenAnchor.TX.Fee}".Log(LogFile);

      return true;
    }

    public override void DetectAnchorTokenInBlock(TX tX)
    {
      TokenAnchor tokenAnchor = TokensAnchorUnconfirmed
        .Find(t => t.TX.Hash.IsEqual(tX.Hash));

      if (tokenAnchor != null)
      {
        $"Detected self mined anchor token {tX} in Bitcoin block".Log(LogFile);

        TokensAnchorUnconfirmed.Remove(tokenAnchor);
      }
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

        index += ID_BTOKEN.Length;

        tokenAnchor = new(tX, index);

        $"Detected foreign mined anchor token {tX} in Bitcoin block.".Log(LogFile);
      }

      ($"IDToken: {tokenAnchor.IDToken}.\n" +
        $"HashBlockReferenced: {tokenAnchor.HashBlockReferenced.ToHexString()}.\n" +
        $"HashBlockPreviousReferenced: {tokenAnchor.HashBlockPreviousReferenced.ToHexString()}.\n" +
        $"ValueChange: {tokenAnchor.ValueChange}.\n" +
        $"NumberSequence: {tokenAnchor.NumberSequence}.\n" +
        $"tx: {tokenAnchor.TX}.\n").Log(LogFile);

      Header headerParent = TokenParent.HeaderTip;
      
      while (
        headerParent.HashChild == null ||
        !headerParent.HashChild.IsEqual(tokenAnchor.HashBlockPreviousReferenced))
      {
        headerParent = headerParent.HeaderPrevious;

        if (headerParent == null)
        {
          $"Anchor token {tokenAnchor} references orphan block {tokenAnchor.HashBlockReferenced.ToHexString()}".Log(LogFile);
          return;
        }
      }

      TokensAnchorDetectedInBlock.Add(tokenAnchor);
    }

    public override void SignalParentBlockInsertion(Header headerAnchor)
    {
      try
      {
        if (TokensAnchorDetectedInBlock.Count > 0)
        {
          headerAnchor.HashChild = GetHashBlockChild(headerAnchor.Hash);

          TokensAnchorDetectedInBlock.Clear();

          if (BlocksMined.Count > 0)
          {
            Block block = BlocksMined.Find(b =>
            b.Header.Hash.IsEqual(headerAnchor.HashChild));

            if (block != null)
            {
              InsertBlock(block);
              Network.AdvertizeBlockToNetwork(block);
            }
          }
        }
      }
      catch (Exception ex)
      {
        ($"{ex.GetType().Name} when signaling Bitcoin block {headerAnchor}" +
          $" with height {headerAnchor.Height} to BToken:\n" +
          $"Exception message: {ex.Message}").Log(this, LogFile);
      }
    }

    public byte[] GetHashBlockChild(byte[] hashHeaderAnchor)
    {
      SHA256 sHA256 = SHA256.Create();

      byte[] targetValue = sHA256.ComputeHash(hashHeaderAnchor);
      byte[] biggestDifferenceTemp = new byte[32];
      TokenAnchor tokenAnchorWinner = null;

      Debug.WriteLine($"Targetvalue {targetValue.ToHexString()}");

      TokensAnchorDetectedInBlock.ForEach(t =>
      {
        byte[] differenceHash = targetValue.SubtractByteWise(
          t.HashBlockReferenced);

        Debug.WriteLine($"differenceHash {differenceHash.ToHexString()} " +
          $"of HashBlockReferenced {t.HashBlockReferenced.ToHexString()} of token {t}");

        if (differenceHash.IsGreaterThan(biggestDifferenceTemp))
        {
          biggestDifferenceTemp = differenceHash;
          tokenAnchorWinner = t;
        }
      });

      ($"The winning anchor token is {tokenAnchorWinner.TX} referencing block " +
        $"{tokenAnchorWinner.HashBlockReferenced.ToHexString()}.").Log(LogFile);

      return tokenAnchorWinner.HashBlockReferenced;
    }
  }
}
