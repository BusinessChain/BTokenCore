using LiteDB;


namespace BTokenCore;

internal class Miner
{
  BlockchainBToken Blockchain;
  Blockchain BlockchainParent;
  Network NetworkParent;
  Anchorer AnchorerParent;

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


  internal Miner(BlockchainBToken blockchain, Blockchain blockchainParent, Network networkParent, Anchorer anchorer)
  {
    Blockchain = blockchain;
    BlockchainParent = blockchainParent;
    NetworkParent = networkParent;
    AnchorerParent = anchorer;
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
      ["funds"] = AnchorerParent.GetFunds(),
      ["isBlockTipAtHeaderTip"] = BlockchainParent.IsBlockTipAtHeaderTip(),
      ["secondsAgeTipBitcoin"] = BlockchainParent.GetSecondsAgeHeader(BlockchainParent.GetHeightHeaderTip())
    };
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

    if (!AnchorerParent.TryCreateTXAnchor(anchorToken, fee, Budget))
      return false;

    block.Serialize();
    Blockchain.StoreBlockAwaitingAnchor(block, BlockchainParent.GetHeight());

    return true;
  }

  internal abstract class Anchorer
  {
    protected Blockchain BlockchainParent;
    protected Network NetworkParent;

    protected ILiteCollection<BsonDocument> AnchorsStored;

    const string NAME_FIELD_TX_RAW = "tXRaw";
    const string NAME_FIELD_FEE = "fee";
    const string NAME_FIELD_HEIGHT_CONFIRMED = "heightConfirmed";
    const string NAME_FIELD_TIME_SENT = "timeSent";


    protected Anchorer(Blockchain blockchainParent, Network networkParent, LiteDatabase liteDatabase)
    {
      BlockchainParent = blockchainParent;
      NetworkParent = networkParent;

      AnchorsStored = liteDatabase.GetCollection<BsonDocument>("anchors");

      blockchainParent.OnBlockInsertedInTransaction += InsertBlockParent;
      blockchainParent.OnBlockRolledBackInTransaction += RollBackBlockParent;
    }

    internal abstract bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor, long budget);

    internal abstract long GetCostAnchor(long feeAnchor);

    internal abstract long GetFunds();

    internal abstract long GetFeeAnchorMin();

    internal abstract long GetStepFeeRaise();

    internal abstract long GetStepFeeDecrease();

    protected abstract bool IsAnchorConfirmed(TX anchor, int height);

    protected abstract bool IsAnchorMissed(TX anchor);

    protected abstract void InsertBlockParentFunds(Block block);

    protected abstract void RollBackBlockParentFunds(Block block);

    void InsertBlockParent(Block block)
    {
      int height = block.Header.Height;

      InsertBlockParentFunds(block);

      foreach (BsonDocument documentAnchor in AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
        if (IsAnchorConfirmed(ParseAnchor(documentAnchor), height))
        {
          documentAnchor[NAME_FIELD_HEIGHT_CONFIRMED] = height;
          AnchorsStored.Update(documentAnchor);

          Console.WriteLine($"Anchor TX {documentAnchor[BTokenCore.Blockchain.NAME_FIELD_ID].AsBinary.ToHexString()} confirmed at {BlockchainParent.NameToken} height {height}.");
        }

      AnchorsStored.DeleteMany(Query.Between(NAME_FIELD_HEIGHT_CONFIRMED, 1, height - BTokenCore.Blockchain.BITCOIN_REORG_MARGIN));
    }

    void RollBackBlockParent(Block block)
    {
      RollBackBlockParentFunds(block);

      foreach (BsonDocument documentAnchor in AnchorsStored.Find(Query.GTE(NAME_FIELD_HEIGHT_CONFIRMED, block.Header.Height)).ToList())
      {
        documentAnchor[NAME_FIELD_HEIGHT_CONFIRMED] = 0;
        AnchorsStored.Update(documentAnchor);
      }
    }

    protected TX ParseAnchor(BsonDocument documentAnchor)
    {
      byte[] tXRaw = documentAnchor[NAME_FIELD_TX_RAW].AsBinary;
      int index = 0;

      TX tX = BlockchainParent.Parser.ParseTX(tXRaw, ref index, isCoinbase: false, verifySignatures: false);

      tX.TXRaw = tXRaw;
      tX.Fee = documentAnchor[NAME_FIELD_FEE].AsInt64;

      return tX;
    }

    protected void InsertAnchor(TX tXAnchor)
    {
      AnchorsStored.Insert(new BsonDocument
      {
        [BTokenCore.Blockchain.NAME_FIELD_ID] = tXAnchor.Hash,
        [NAME_FIELD_TX_RAW] = tXAnchor.TXRaw,
        [NAME_FIELD_FEE] = tXAnchor.Fee,
        [NAME_FIELD_HEIGHT_CONFIRMED] = 0,
        [NAME_FIELD_TIME_SENT] = DateTime.UtcNow.Ticks
      });
    }

    protected List<TX> LoadAnchorsUnconfirmed()
    {
      return AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
        .Select(ParseAnchor)
        .ToList();
    }

    internal int CountAnchorsMissed(DateTime timeSentLatest)
    {
      return AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
        .Where(d => d[NAME_FIELD_TIME_SENT].AsInt64 <= timeSentLatest.Ticks)
        .Select(ParseAnchor)
        .Count(IsAnchorMissed);
    }
  }
}
