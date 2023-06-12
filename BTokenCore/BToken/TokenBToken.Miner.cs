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
    const int TIMESPAN_MINING_LOOP_MILLISECONDS = 1 * 5000;
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

      while (IsMining)
      {
        int timeMSLoop = TIMESPAN_MINING_LOOP_MILLISECONDS;

        if (TryLock())
        {
          if (TryMineAnchorToken(out TokenAnchor tokenAnchor))
          {
            // timeMSLoop = (int)(tokenAnchor.TX.Fee * TIMESPAN_DAY_SECONDS * 1000 /
            // COUNT_SATOSHIS_PER_DAY_MINING);

            // timeMSLoop = RandomGeneratorMiner.Next(
            // timeMSCreateNextAnchorToken / 2,
            // timeMSCreateNextAnchorToken * 3 / 2);
          }

          ReleaseLock();
        }

        await Task.Delay(timeMSLoop).ConfigureAwait(false);
      }

      $"Exit BToken miner.".Log(LogFile);
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

      block.TXs.ForEach(t => { block.Buffer = block.Buffer.Concat(t.TXRaw).ToArray(); });

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
      TokensAnchorUnconfirmed.Add(tokenAnchor);

      // Immer bevor ein Token an einen Peer advertized wird,
      // fragt man den Peer ob er die Ancestor TX schon hat.
      // Wenn nicht iterativ weiterfragen und dann alle Tokens schicken.

      TokenParent.BroadcastTX(tokenAnchor.TX);

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

        $"Detected foreign - mined anchor token {tX} in Bitcoin block".Log(LogFile);

        index += ID_BTOKEN.Length;

        tokenAnchor = new(tX, index);
      }

      tokenAnchor.IsConfirmed = true;
      TokensAnchorDetectedInBlock.Add(tokenAnchor);

      $"Anchor token references {tokenAnchor.HashBlockReferenced.ToHexString()}".Log(LogFile);
    }

    public override void SignalCompletionBlockInsertion(Header headerAnchor)
    {
      try
      {
        $"{TokensAnchorDetectedInBlock.Count} anchor tokens detected in Bitcoin block {headerAnchor}."
          .Log(LogFile);

        if (TokensAnchorDetectedInBlock.Count > 0)
        {
          TokenAnchor tokenAnchorWinner = GetTXAnchorWinner(headerAnchor);

          Block block = null;

          if (Archiver.TryLoadBlockArchive(
            headerAnchor.Height, out byte[] buffer))
          {
            block = CreateBlock();

            block.Buffer = buffer;
            block.Parse();
          }
          else if (BlocksMined.Count > 0)
          {
            block = BlocksMined.Find(b =>
            b.Header.Hash.IsEqual(tokenAnchorWinner.HashBlockReferenced));

            if (TokensAnchorUnconfirmed.Count == 0)
            {
              NumberSequence = 0;
              FeeSatoshiPerByte /= FACTOR_INCREMENT_FEE_PER_BYTE;
            }
          }

          if (block != null)
            InsertBlock(block);
        }

        TokensAnchorDetectedInBlock.Clear();
        BlocksMined.Clear();

        if (TokensAnchorUnconfirmed.Count > 0)
        {
          FeeSatoshiPerByte *= FACTOR_INCREMENT_FEE_PER_BYTE;

          $"{TokensAnchorUnconfirmed.Count} anchor tokens unconfirmed, do RBF."
            .Log(LogFile);

          NumberSequence += 1;

          RBFAnchorTokens();
        }

        $"New fee per byte is {FeeSatoshiPerByte}.".Log(LogFile);
      }
      catch (Exception ex)
      {
        ($"{ex.GetType().Name} when signaling Bitcoin block {headerAnchor}" +
          $" with height {headerAnchor.Height} to BToken.\n" +
          $"Exception message: {ex.Message}").Log(this, LogFile);
      }
    }

    public TokenAnchor GetTXAnchorWinner(Header headerAnchor)
    {
      SHA256 sHA256 = SHA256.Create();

      byte[] targetValue = sHA256.ComputeHash(headerAnchor.Hash);
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

      if (TrailAnchorChain.ContainsValue(headerAnchor.Height))
        throw new InvalidOperationException(
          "Cannot have entries with the same hight in anchor trail.");

      TrailAnchorChain.Add(
        tokenAnchorWinner.HashBlockReferenced,
        headerAnchor.Height);

      return tokenAnchorWinner;
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

    void LoadMinedunconfirmed()
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
  }
}
