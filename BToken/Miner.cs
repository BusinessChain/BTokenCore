namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  internal class Miner
  {
    BlockchainBToken Blockchain;
    Blockchain BlockchainParent;
    Network NetworkParent;
    Blockchain.Anchorer AnchorerParent;
    internal Wallet Wallet;

    const int SECONDS_PERIOD_BUDGET = 600;
    const int SECONDS_TEMPLATE_REFRESH = 30;

    Timer TimerMining;
    long Budget;
    uint NonceBlockNext;
    string StateRoundLast = "";
    string OutcomeAnchorsLast = "";
    int HeightTipParentRound = -1;
    byte[] HashTipRound;
    int CountAnchorsRound;
    long FeeAnchor;


    internal Miner(BlockchainBToken blockchain, Blockchain blockchainParent, Network networkParent, Blockchain.Anchorer anchorer)
    {
      Blockchain = blockchain;
      BlockchainParent = blockchainParent;
      NetworkParent = networkParent;
      AnchorerParent = anchorer;
      Wallet = new Wallet(blockchain.NameToken);
      Wallet.AddKeys(CreateKeysCoveringSegments());
      NonceBlockNext = (uint)Random.Shared.Next();

      blockchainParent.OnBlockInserted += HandleParentTip;
      blockchain.OnBlockInserted += block => ScheduleReanchor(block.Header.Hash);
      blockchain.OnBlockRolledBack += block => ScheduleReanchor(block.Header.HashPrevious);
    }

    List<string> CreateKeysCoveringSegments()
    {
      HashSet<int> indexesCovered = Wallet.GetHash160sKey().Select(DatabaseAccounts.GetIndexSegment).ToHashSet();
      List<string> keysNew = [];

      while (indexesCovered.Count < COUNT_SEGMENTS)
      {
        string keyPrivateDecimal = Crypto.GenerateKeyPrivate();

        if (indexesCovered.Add(DatabaseAccounts.GetIndexSegment(Crypto.GetHash160KeyPublic(keyPrivateDecimal))))
          keysNew.Add(keyPrivateDecimal);
      }

      return keysNew;
    }

    internal Dictionary<string, object> GetSnapshotState()
    {
      return new Dictionary<string, object>
      {
        ["isMining"] = TimerMining != null,
        ["budget"] = Budget,
        ["feeAnchor"] = FeeAnchor,
        ["stateRoundLast"] = StateRoundLast,
        ["funds"] = AnchorerParent.GetFunds(),
        ["addressFunding"] = AnchorerParent.Wallet.AddressAccountReceiving,
        ["nameTokenParent"] = BlockchainParent.NameToken,
        ["isBlockTipAtHeaderTip"] = BlockchainParent.IsBlockTipAtHeaderTip(),
        ["isPeerConnected"] = NetworkParent.IsPeerConnected()
      };
    }

    internal List<TXBToken> CreateTXsRevenue(byte[] iDAccountTarget, long fee)
    {
      List<TXBToken> tXs = [];

      foreach (byte[] hash160 in Wallet.GetHash160sKey())
      {
        if (Blockchain.GetBalanceAccount(hash160) is not long balance || balance <= fee)
          continue;

        TXBToken tX = Blockchain.CreateTXBTokenUnsigned(Wallet.GetKeyPublic(hash160), fee, [new TXOutputP2PKH(balance - fee, iDAccountTarget)]);
        tX.Serialize(Wallet, Blockchain.IDToken);

        tXs.Add(tX);
      }

      return tXs;
    }

    internal void StartMining(long budget)
    {
      long feeAnchorMin = AnchorerParent.GetFeeAnchorMin();

      if (budget < feeAnchorMin)
        throw new ProtocolException($"Budget {budget} must be at least {feeAnchorMin} satoshis.");

      Budget = budget;
      FeeAnchor = feeAnchorMin;

      if (TimerMining != null)
        return;

      TimerMining = new Timer(
        _ => Blockchain.RunLocked(() => RunTimer(() => MineBlock(0))),
        null,
        0,
        Timeout.Infinite);
    }

    internal void StopMining()
    {
      TimerMining?.Dispose();
      TimerMining = null;
    }

    void HandleParentTip(Block blockParent)
    {
      if (TimerMining == null || blockParent.Header.HeaderNext != null)
        return;

      int heightParentTip = blockParent.Header.Height;

      if (HeightTipParentRound == heightParentTip - 1)
        if (AnchorerParent.CountAnchorsMissed(DateTime.UtcNow.AddSeconds(-SECONDS_TEMPLATE_REFRESH)) > 0)
        {
          OutcomeAnchorsLast = ", after a missed anchor";
          FeeAnchor = Math.Min(Budget, FeeAnchor + AnchorerParent.GetStepFeeRaise());
        }
        else
        {
          OutcomeAnchorsLast = ", after the last anchors were mined";
          FeeAnchor = Math.Max(AnchorerParent.GetFeeAnchorMin(), FeeAnchor - AnchorerParent.GetStepFeeDecrease());
        }

      int countAnchorsMissed = AnchorerParent.CountAnchorsMissed(DateTime.MaxValue);

      if (countAnchorsMissed > 0)
        RunTimer(() => MineBlock(countAnchorsMissed));
    }

    void ScheduleReanchor(byte[] hashTip)
    {
      if (BlockchainParent.GetHeight() == HeightTipParentRound
        && !hashTip.IsAllBytesEqual(HashTipRound))
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

      double millisecondsDelayMean = (double)AnchorerParent.GetCostAnchor(FeeAnchor) * SECONDS_PERIOD_BUDGET * 1000 / Budget;
      double millisecondsDelay = -millisecondsDelayMean * Math.Log(1 - Random.Shared.NextDouble());

      TimerMining?.Change((long)Math.Min(millisecondsDelay, uint.MaxValue - 1), Timeout.Infinite);
    }

    void ReportRound(string state)
    {
      StateRoundLast = state;
      Console.WriteLine(state);
    }

    void MineBlock(int countAnchorsMissed)
    {
      int height = Blockchain.GetHeight() + 1;
      string prefixSkip = $"Mining round at height {height} skipped: ";

      string nameTokenParent = BlockchainParent.NameToken;

      if (!BlockchainParent.IsBlockTipAtHeaderTip())
      {
        ReportRound(prefixSkip + $"the {nameTokenParent} blocks are not at the {nameTokenParent} header tip.");
        return;
      }

      if (!NetworkParent.IsPeerConnected())
        return;

      Block block;

      try
      {
        block = Blockchain.CreateBlockNext(Wallet.GetHash160KeyReceiving(Blockchain.TXPool.CanReceive));
      }
      catch (ProtocolException exception)
      {
        ReportRound(prefixSkip + exception.Message);
        return;
      }

      Header header = block.Header;

      int heightParent = BlockchainParent.GetHeight();

      if (heightParent != HeightTipParentRound)
        CountAnchorsRound = 0;

      bool isReanchor = CountAnchorsRound > 0 && !header.HashPrevious.IsAllBytesEqual(HashTipRound);
      int countAnchors = isReanchor ? CountAnchorsRound : Math.Max(1, countAnchorsMissed);

      int countAnchorsSent = 0;

      while (countAnchorsSent < countAnchors && TryAnchorBlock(block, FeeAnchor))
        countAnchorsSent++;

      if (countAnchorsSent == 0)
      {
        ReportRound(prefixSkip + $"too few funds for the anchor fee {FeeAnchor}.");
        return;
      }

      HeightTipParentRound = heightParent;
      HashTipRound = header.HashPrevious;

      if (isReanchor)
      {
        ReportRound($"{countAnchorsSent} of {countAnchors} anchors of {nameTokenParent} round {heightParent} moved to the new tip at height {height}, anchor fee {FeeAnchor}.");
        return;
      }

      CountAnchorsRound += countAnchorsSent;

      ReportRound(countAnchorsMissed > 0
        ? $"{countAnchorsSent} of {countAnchorsMissed} missed anchors replaced by anchors of {nameTokenParent} round {heightParent} for height {height}, anchor fee {FeeAnchor}{OutcomeAnchorsLast}."
        : $"Anchor {CountAnchorsRound} of {nameTokenParent} round {heightParent} sent for height {height}, anchor fee {FeeAnchor}{OutcomeAnchorsLast}.");

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

      if (!AnchorerParent.TryCreateTXAnchor(anchorToken, fee, Budget))
        return false;

      block.Serialize();
      Blockchain.StoreBlockAwaitingAnchor(block, BlockchainParent.GetHeight());

      return true;
    }
  }
}
