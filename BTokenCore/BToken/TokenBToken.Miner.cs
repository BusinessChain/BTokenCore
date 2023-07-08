using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  partial class TokenBToken : Token
  {
    const int COUNT_TXS_PER_BLOCK_MAX = 5;
    const int TIMESPAN_MINING_ANCHOR_TOKENS_MILLISECONDS = 20 * 1000;
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

    const int TIME_MINER_PAUSE_SECONDS = 5;



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

      while (IsMining)
      {
        int timeMSLoop = TIMESPAN_MINING_ANCHOR_TOKENS_MILLISECONDS;

        // was ist, wenn es gerade zwei blöcke, oder fork gemacht hat?
        // wo wird auf btk block gewartet?

        if (TryLock())
        {
          if (headerTipParent == null)
          {
            headerTipParent = TokenParent.HeaderTip;
            headerTip = HeaderTip;
          }
          
          if(headerTipParent == TokenParent.HeaderTip)
          {
            if (TryMineAnchorToken(out TokenAnchor tokenAnchor))
            {
              TokensAnchorUnconfirmed.Add(tokenAnchor);



              // Immer bevor ein Token an einen Peer advertized wird,
              // fragt man den Peer ob er die Ancestor TX schon hat.
              // Wenn nicht iterativ weiterfragen und dann alle Tokens schicken.

              TokenParent.BroadcastTX(tokenAnchor.TX);

              // timeMSLoop = (int)(tokenAnchor.TX.Fee * TIMESPAN_DAY_SECONDS * 1000 /
              // COUNT_SATOSHIS_PER_DAY_MINING);

              // timeMSLoop = RandomGeneratorMiner.Next(
              // timeMSCreateNextAnchorToken / 2,
              // timeMSCreateNextAnchorToken * 3 / 2);
            }
          }
          else
          {
            headerTipParent = TokenParent.HeaderTip;

            for (int i = 0; i < 100; i += 1)
            {
              while (headerTip.HeaderNext != null)
              {
                headerTip = headerTip.HeaderNext;

                if (headerTip.Hash.IsEqual(headerTipParent.HashChild))
                  break;
              }

              await Task.Delay(100);
            }

            if (TokensAnchorUnconfirmed.Count > 0)
            {
              FeeSatoshiPerByte *= FACTOR_INCREMENT_FEE_PER_BYTE;
              NumberSequence += 1;

              RBFAnchorTokens();
            }

          }


          ReleaseLock();
        }

        await Task.Delay(timeMSLoop).ConfigureAwait(false);
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
        if (!TryMineAnchorToken(out TokenAnchor tokenAnchor))
          break;
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
      {
        ($"Miner wallet has not enough value {valueAccrued} to " +
          $"pay for anchor token fee {feeAccrued}").Log(LogFile);

        return false;
      }

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

      ($"BToken miner successfully mined anchor Token {tokenAnchor.TX} with fee {tokenAnchor.TX.Fee}.\n" +
        $"{TokensAnchorUnconfirmed.Count} mined unconfirmed anchor tokens.")
        .Log(LogFile);

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
      }

      Header headerParent = TokenParent.HeaderTip;
      while (
        headerParent.HashChild == null ||
        !tokenAnchor.HashBlockPreviousReferenced.IsEqual(headerParent.HashChild))
      {
        headerParent = headerParent.HeaderPrevious;

        if (headerParent == null)
          return;
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
            if (TokensAnchorUnconfirmed.Count == 0)
            {
              FeeSatoshiPerByte /= FACTOR_INCREMENT_FEE_PER_BYTE;
              NumberSequence = 0;
            }

            Block block = BlocksMined.Find(b =>
            b.Header.Hash.IsEqual(headerAnchor.HashChild));

            BlocksMined.Clear();

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

      ($"The winning anchor token is {tokenAnchorWinner.TX} referencing block " +
        $"{tokenAnchorWinner.HashBlockReferenced.ToHexString()}.").Log(LogFile);

      return tokenAnchorWinner.HashBlockReferenced;
    }
  }
}
