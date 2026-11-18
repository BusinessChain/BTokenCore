namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  protected class Miner
  {
    BlockchainBToken Blockchain;

    const int MILLISECONDS_WINDOW_BLANK = 5_000;
    const double FEE_PER_BYTE_DECREASE_ANCHOR = 0.5;

    Timer TimerMining;
    long Budget;
    uint NonceBlockNext;
    string StateRoundLast = "";
    int HeightTipParentRound = -1;
    int CountAnchorsRound = 1;
    byte[] HashTipRound;
    long FeeAnchor;
    long FeeRound;
    long Savings;


    internal Miner(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
      NonceBlockNext = (uint)Random.Shared.Next();
    }

    internal Dictionary<string, object> GetSnapshotState()
    {
      return new Dictionary<string, object>
      {
        ["isMining"] = TimerMining != null,
        ["budget"] = Budget,
        ["feeAnchor"] = FeeAnchor,
        ["savings"] = Savings,
        ["stateRoundLast"] = StateRoundLast
      };
    }

    internal void StartMining(long budget)
    {
      if (budget < BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR)
        throw new ProtocolException($"Budget {budget} must be at least {BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR} satoshis.");

      Budget = budget;
      FeeAnchor = budget;
      Savings = 0;

      if (TimerMining != null)
        return;

      TimerMining = new Timer(
        _ => Blockchain.RunLocked(() => RunTimer(MineBlock)),
        null,
        0,
        Timeout.Infinite);
    }

    internal void StopMining()
    {
      TimerMining?.Dispose();
      TimerMining = null;
    }

    internal void ScheduleMining(bool isAnchorWinnerForeign)
    {
      TimerMining?.Change(isAnchorWinnerForeign ? MILLISECONDS_WINDOW_BLANK : 0, Timeout.Infinite);
    }

    internal void ScheduleReanchor()
    {
      if (Blockchain.BlockchainParent.GetHeight() == HeightTipParentRound
        && !Blockchain.ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(HashTipRound))
        TimerMining?.Change(0, Timeout.Infinite);
    }

    void RunTimer(Action action)
    {
      if (TimerMining == null)
        return;

      try
      {
        action();
      }
      catch (Exception exception)
      {
        StateRoundLast = $"Mining stopped after {exception.GetType().Name}: {exception.Message}";
        Console.WriteLine($"Warning: {StateRoundLast}");
        StopMining();
      }
    }

    TXBToken CreateTXCoinbase(int height, long fees, long rent)
    {
      TXOutputP2PKH outputCoinbase = new(GetBlockReward(height) + fees + rent, Blockchain.Wallet.GetHash160KeyReceiving(Blockchain.TXPool.CanReceive));

      TXBToken tXCoinbase = new()
      {
        HeightAccountCreated = height
      };

      tXCoinbase.TXOutputs.Add(outputCoinbase);

      tXCoinbase.SerializeUnsigned();

      return tXCoinbase;
    }

    internal void MineBlock()
    {
      int height = Blockchain.ChainRoot.HeaderTipBlockchain.Height + 1;
      string prefixSkip = $"Mining round at height {height} skipped: ";

      if (!Blockchain.Synchronizer.IsAccountsAtTip())
      {
        StateRoundLast = prefixSkip + "the accounts are not at the tip.";
        return;
      }

      if (!Blockchain.BlockchainParent.IsBlockTipAtHeaderTip())
      {
        StateRoundLast = prefixSkip + "the Bitcoin blocks are not at the Bitcoin header tip.";
        return;
      }

      long feeAnchor = FeeAnchor;
      long feeRound = FeeRound;
      long savings = Savings;
      int countAnchorsRound = CountAnchorsRound;

      if (Blockchain.BlockchainParent.GetHeight() != HeightTipParentRound)
      {
        countAnchorsRound = 1;
        feeRound = feeAnchor;

        if (HeightTipParentRound >= 0)
          if (Blockchain.BlockchainParent.HasAnchorMissed())
          {
            feeAnchor = Budget;
            feeRound = Budget + savings;
            savings = 0;
          }
          else
          {
            savings += Budget - feeAnchor;
            feeAnchor = Math.Max(BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR, feeAnchor - Math.Max(1, (long)(FEE_PER_BYTE_DECREASE_ANCHOR * BlockchainBitcoin.LENGTH_ANCHOR)));
            feeRound = feeAnchor;

            if (savings >= feeAnchor * BlockchainBitcoin.LENGTH_OUTPUT_ANCHOR / BlockchainBitcoin.LENGTH_ANCHOR)
            {
              countAnchorsRound = 2;
              feeRound = feeAnchor + savings;
              savings = 0;
            }
          }
      }

      Block block = new(Blockchain.SizeBlockMax);

      int countBytesTXsMax = block.Buffer.Length
        - HeaderBToken.COUNT_HEADER_BYTES
        - VarInt.GetBytes(int.MaxValue).Length
        - TXBToken.LENGTH_TX_COINBASE;

      block.TXs = Blockchain.TXPool.GetTXs(countBytesTXsMax);

      block.TXs.Insert(0, CreateTXCoinbase(height, block.TXs.Sum(t => t.Fee), Blockchain.Accounts.LoadRentSweep(height)));

      HeaderBToken header = new()
      {
        Height = height,
        HashPrevious = Blockchain.ChainRoot.HeaderTipBlockchain.Hash,
        MerkleRoot = block.ComputeMerkleRoot(),
        HashAnchors = ParserToken.ComputeHashAnchors(ParserToken.EnumerateAnchorsEligible(block.TXs, height))
      };

      block.Header = header;

      try
      {
        header.HashAccounts = Blockchain.Accounts.ComputeHash(block);
      }
      catch (ProtocolException exception)
      {
        StateRoundLast = prefixSkip + exception.Message;
        Console.WriteLine(StateRoundLast);
        return;
      }

      List<Block> blocksAnchored = [];
      List<TXOutputTokenAnchor> anchorsToken = [];

      for (int i = 0; i < countAnchorsRound; i++)
      {
        Block blockAnchored = new(Blockchain.SizeBlockMax)
        {
          TXs = block.TXs,
          Header = new HeaderBToken()
          {
            Height = height,
            HashPrevious = header.HashPrevious,
            MerkleRoot = header.MerkleRoot,
            HashAnchors = header.HashAnchors,
            HashAccounts = header.HashAccounts,
            Nonce = NonceBlockNext++
          }
        };

        blockAnchored.Header.ComputeHash();

        blocksAnchored.Add(blockAnchored);

        anchorsToken.Add(new()
        {
          IDToken = Blockchain.IDToken,
          HashBlockPreviousReferenced = header.HashPrevious,
          HashBlockReferenced = blockAnchored.Header.Hash
        });
      }

      if (!Blockchain.BlockchainParent.TryCreateTXAnchor(anchorsToken, feeRound, out int lockTimeAnchor))
      {
        StateRoundLast = prefixSkip + $"no Bitcoin wallet output funds the anchor fee {feeRound}.";
        return;
      }

      FeeAnchor = feeAnchor;
      FeeRound = feeRound;
      Savings = savings;
      CountAnchorsRound = countAnchorsRound;
      HeightTipParentRound = lockTimeAnchor;
      HashTipRound = header.HashPrevious;

      foreach (Block blockAnchored in blocksAnchored)
      {
        blockAnchored.Serialize();
        Blockchain.StoreBlockAwaitingAnchor(blockAnchored, lockTimeAnchor);
      }

      StateRoundLast = $"{blocksAnchored.Count} blocks at height {height} await their anchors.";
    }
  }
}
