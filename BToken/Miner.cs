namespace BTokenCore;

internal class Miner
{
  BlockchainBToken Blockchain;
  Blockchain BlockchainParent;
  Network NetworkParent;

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


  internal Miner(BlockchainBToken blockchain, Blockchain blockchainParent, Network networkParent)
  {
    Blockchain = blockchain;
    BlockchainParent = blockchainParent;
    NetworkParent = networkParent;
    NonceBlockNext = (uint)Random.Shared.Next();

    blockchainParent.OnBlockInserted += HandleParentTip;
    blockchain.OnBlockInserted += block => ScheduleReanchor(block.Header.Hash);
    blockchain.OnBlockRolledBack += block => ScheduleReanchor(block.Header.HashPrevious);
  }

  internal Dictionary<string, object> GetSnapshotState()
  {
    return new Dictionary<string, object>
    {
      ["isMining"] = TimerMining != null,
      ["budget"] = Budget,
      ["feeAnchor"] = FeeAnchor,
      ["stateRoundLast"] = StateRoundLast,
      ["funds"] = ((BlockchainBitcoin)BlockchainParent).GetFunds(),
      ["isBlockTipAtHeaderTip"] = BlockchainParent.IsBlockTipAtHeaderTip(),
      ["secondsAgeTipBitcoin"] = ((BlockchainBitcoin)BlockchainParent).GetSecondsAgeHeaderTip()
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
      if (BlockchainParent.CountAnchorsMissed(DateTime.UtcNow.AddSeconds(-SECONDS_TEMPLATE_REFRESH)) > 0)
      {
        OutcomeAnchorsLast = ", after a missed anchor";
        FeeAnchor = Math.Min(Budget, FeeAnchor + (long)(FEE_PER_BYTE_RAISE_ANCHOR * BlockchainBitcoin.LENGTH_ANCHOR));
      }
      else
      {
        OutcomeAnchorsLast = ", after the last anchors were mined";
        FeeAnchor = Math.Max(BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR, FeeAnchor - Math.Max(1, (long)(FEE_PER_BYTE_DECREASE_ANCHOR * BlockchainBitcoin.LENGTH_ANCHOR)));
      }

    int countAnchorsMissed = BlockchainParent.CountAnchorsMissed(DateTime.MaxValue);

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

    double millisecondsDelayMean = (double)BlockchainParent.GetCostAnchor(FeeAnchor) * SECONDS_PERIOD_BUDGET * 1000 / Budget;
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

    if (!BlockchainParent.IsBlockTipAtHeaderTip())
    {
      ReportRound(prefixSkip + "the Bitcoin blocks are not at the Bitcoin header tip.");
      return;
    }

    if (!NetworkParent.IsPeerConnected())
    {
      ReportRound(prefixSkip + "no Bitcoin peer is connected.");
      return;
    }

    Block block;

    try
    {
      block = Blockchain.CreateBlockNext();
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

    CountAnchorsRound += countAnchorsSent;

    ReportRound(countAnchorsMissed > 0
      ? $"{countAnchorsSent} of {countAnchorsMissed} missed anchors replaced by anchors of Bitcoin round {heightParent} for height {height}, anchor fee {FeeAnchor}{OutcomeAnchorsLast}."
      : $"Anchor {CountAnchorsRound} of Bitcoin round {heightParent} sent for height {height}, anchor fee {FeeAnchor}{OutcomeAnchorsLast}.");

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

    if (!BlockchainParent.TryCreateTXAnchor(anchorToken, fee, Budget, out byte[] tXRaw))
      return false;

    NetworkParent.SendTX(tXRaw);

    block.Serialize();
    Blockchain.StoreBlockAwaitingAnchor(block, BlockchainParent.GetHeight());

    return true;
  }
}
