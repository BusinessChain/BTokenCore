namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  protected class Miner
  {
    BlockchainBToken Blockchain;

    const int SECONDS_PERIOD_BUDGET = 600;
    const int SECONDS_TEMPLATE_REFRESH = 30;
    const double FEE_PER_BYTE_RAISE_ANCHOR = 1;
    const double FEE_PER_BYTE_DECREASE_ANCHOR = 0.1;

    Timer TimerMining;
    long Budget;
    uint NonceBlockNext;
    string StateRoundLast = "";
    string OutcomeAnchorsLast = "";
    int HeightTipParentRound = -1;
    byte[] HashTipRound;
    int CountAnchorsRound;
    long FeeAnchor;


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
        ["stateRoundLast"] = StateRoundLast,
        ["isAccountsAtTip"] = Blockchain.Synchronizer.IsAccountsAtTip(),
        ["isBlockTipAtHeaderTip"] = Blockchain.BlockchainParent.IsBlockTipAtHeaderTip(),
        ["secondsAgeTipBitcoin"] = ((BlockchainBitcoin)Blockchain.BlockchainParent).GetSecondsAgeHeaderTip()
      };
    }

    internal void StartMining(long budget)
    {
      if (budget < BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR)
        throw new ProtocolException($"Budget {budget} must be at least {BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR} satoshis.");

      Budget = budget;
      FeeAnchor = BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR;

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

    internal void AdjustFeeAnchor(int heightParentTip)
    {
      if (TimerMining == null)
        return;

      if (HeightTipParentRound == heightParentTip - 1)
        if (Blockchain.BlockchainParent.HasAnchorMissed(DateTime.UtcNow.AddSeconds(-SECONDS_TEMPLATE_REFRESH)))
        {
          OutcomeAnchorsLast = ", after a missed anchor";
          FeeAnchor = Math.Min(Budget, FeeAnchor + (long)(FEE_PER_BYTE_RAISE_ANCHOR * BlockchainBitcoin.LENGTH_ANCHOR));
        }
        else
        {
          OutcomeAnchorsLast = ", after the last anchors were mined";
          FeeAnchor = Math.Max(BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR, FeeAnchor - Math.Max(1, (long)(FEE_PER_BYTE_DECREASE_ANCHOR * BlockchainBitcoin.LENGTH_ANCHOR)));
        }
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
        ReportRound($"Mining stopped after {exception.GetType().Name}: {exception.Message}");
        StopMining();
        return;
      }

      double millisecondsDelayMean = (double)Blockchain.BlockchainParent.GetCostAnchor(FeeAnchor) * SECONDS_PERIOD_BUDGET * 1000 / Budget;
      double millisecondsDelay = -millisecondsDelayMean * Math.Log(1 - Random.Shared.NextDouble());

      TimerMining?.Change((long)Math.Min(millisecondsDelay, uint.MaxValue - 1), Timeout.Infinite);
    }

    void ReportRound(string state)
    {
      StateRoundLast = state;
      Console.WriteLine(state);
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
        ReportRound(prefixSkip + "the accounts are not at the tip.");
        return;
      }

      if (!Blockchain.BlockchainParent.IsBlockTipAtHeaderTip())
      {
        ReportRound(prefixSkip + "the Bitcoin blocks are not at the Bitcoin header tip.");
        return;
      }

      int heightParent = Blockchain.BlockchainParent.GetHeight();

      if (heightParent != HeightTipParentRound)
        CountAnchorsRound = 0;

      bool isReanchor = CountAnchorsRound > 0 && !Blockchain.ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(HashTipRound);
      int countAnchors = isReanchor ? CountAnchorsRound : 1;

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
        ReportRound(prefixSkip + exception.Message);
        return;
      }

      int countAnchorsSent = 0;

      while (countAnchorsSent < countAnchors && TryAnchorBlock(block, FeeAnchor))
        countAnchorsSent++;

      if (countAnchorsSent == 0)
      {
        ReportRound(prefixSkip + $"no Bitcoin wallet output funds the anchor fee {FeeAnchor}.");
        return;
      }

      HeightTipParentRound = heightParent;
      HashTipRound = header.HashPrevious;

      if (isReanchor)
      {
        ReportRound($"{countAnchorsSent} of {countAnchors} anchors of Bitcoin round {heightParent} moved to the new tip at height {height}, anchor fee {FeeAnchor}.");
        return;
      }

      CountAnchorsRound++;

      ReportRound($"Anchor {CountAnchorsRound} of Bitcoin round {heightParent} sent for height {height}, anchor fee {FeeAnchor}{OutcomeAnchorsLast}.");

      OutcomeAnchorsLast = "";
    }

    bool TryAnchorBlock(Block block, long fee)
    {
      block.Header.Nonce = NonceBlockNext++;
      block.Header.ComputeHash();

      TXOutputTokenAnchor anchorToken = new()
      {
        IDToken = Blockchain.IDToken,
        HashBlockPreviousReferenced = block.Header.HashPrevious,
        HashBlockReferenced = block.Header.Hash
      };

      if (!Blockchain.BlockchainParent.TryCreateTXAnchor(anchorToken, fee, Budget))
        return false;

      block.Serialize();
      Blockchain.StoreBlockAwaitingAnchor(block, Blockchain.BlockchainParent.GetHeight());

      return true;
    }
  }
}
