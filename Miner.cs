using LiteDB;
using TXBitcoin = BTokenCore.BlockchainBitcoin.TXBitcoin;
using TXInputBitcoin = BTokenCore.BlockchainBitcoin.TXInputBitcoin;


namespace BTokenCore;

internal class Miner
{
  BlockchainBToken Blockchain;
  BlockchainBitcoin BlockchainParent;
  NetworkBitcoin NetworkParent;

  const int SECONDS_PERIOD_BUDGET = 600;
  const int SECONDS_TEMPLATE_REFRESH = 30;
  const double FEE_PER_BYTE_RAISE_ANCHOR = 1;
  const double FEE_PER_BYTE_DECREASE_ANCHOR = 0.1;

  const int LENGTH_P2PKH_INPUT = 148;
  const int LENGTH_P2PKH_OUTPUT = 34;
  const int LENGTH_TX_OVERHEAD = 10;
  const int LENGTH_OUTPUT_OVERHEAD = 9;
  const int LENGTH_ANCHOR = LENGTH_TX_OVERHEAD
    + LENGTH_P2PKH_INPUT
    + LENGTH_OUTPUT_OVERHEAD
    + TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN
    + LENGTH_P2PKH_OUTPUT;

  const long FEE_PER_BYTE_INCREMENTAL_RELAY = 1;
  const long FEE_SURCHARGE_REPLACEMENT_ANCHOR = FEE_PER_BYTE_INCREMENTAL_RELAY * LENGTH_ANCHOR;

  const long VALUE_DUST_P2PKH = 546;

  const int COUNT_INPUTS_MAX = 252;

  const string NAME_FIELD_TX_RAW = "tXRaw";
  const string NAME_FIELD_FEE = "fee";
  const string NAME_FIELD_HEIGHT_CONFIRMED = "heightConfirmed";
  const string NAME_FIELD_TIME_SENT = "timeSent";

  ILiteCollection<TXOutputOwned> OutputsOwnedStored;
  ILiteCollection<BsonDocument> AnchorsStored;

  Timer TimerMining;
  long Budget;
  uint NonceBlockNext;
  string StateRoundLast = "";
  string OutcomeAnchorsLast = "";
  int HeightTipParentRound = -1;
  byte[] HashTipRound;
  int CountAnchorsRound;
  long FeeAnchor;


  internal Miner(BlockchainBToken blockchain, BlockchainBitcoin blockchainParent, NetworkBitcoin networkParent, LiteDatabase liteDatabaseParent)
  {
    Blockchain = blockchain;
    BlockchainParent = blockchainParent;
    NetworkParent = networkParent;
    NonceBlockNext = (uint)Random.Shared.Next();

    OutputsOwnedStored = liteDatabaseParent.GetCollection<TXOutputOwned>("outputsOwned");
    AnchorsStored = liteDatabaseParent.GetCollection<BsonDocument>("anchors");

    blockchainParent.OnBlockInsertedInTransaction += InsertBlockParent;
    blockchainParent.OnBlockRolledBackInTransaction += RollBackBlockParent;
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
      ["funds"] = GetFunds(),
      ["isBlockTipAtHeaderTip"] = BlockchainParent.IsBlockTipAtHeaderTip(),
      ["secondsAgeTipBitcoin"] = BlockchainParent.GetSecondsAgeHeaderTip()
    };
  }

  internal void StartMining(long budget)
  {
    if (budget < FEE_SURCHARGE_REPLACEMENT_ANCHOR)
      throw new ProtocolException($"Budget {budget} must be at least {FEE_SURCHARGE_REPLACEMENT_ANCHOR} satoshis.");

    Budget = budget;
    FeeAnchor = FEE_SURCHARGE_REPLACEMENT_ANCHOR;

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

  void InsertBlockParent(Block block)
  {
    int height = block.Header.Height;

    foreach (TXBitcoin tX in block.TXs)
    {
      foreach (TXInputBitcoin tXInput in tX.Inputs)
        if (OutputsOwnedStored.FindById(TXOutputOwned.CreateID(tXInput.TXIDOutput, tXInput.IndexOutput)) is TXOutputOwned outputSpent)
        {
          outputSpent.HeightSpent = height;
          OutputsOwnedStored.Update(outputSpent);
        }

      for (int i = 0; i < tX.TXOutputs.Count; i++)
        if (tX.TXOutputs[i] is TXOutputP2PKH tXOutput && BlockchainParent.Wallet.IsKeyOwned(tXOutput.IDAccount))
          OutputsOwnedStored.Insert(new TXOutputOwned
          {
            ID = TXOutputOwned.CreateID(tX.Hash, i),
            TXID = tX.Hash,
            Index = i,
            IDAccount = tXOutput.IDAccount,
            Value = tXOutput.Value,
            HeightCreated = height
          });
    }

    foreach (BsonDocument documentAnchor in AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
      if (ParseAnchor(documentAnchor).Inputs.Any(i =>
        OutputsOwnedStored.FindById(TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput)) is TXOutputOwned output
        && output.HeightSpent == height))
      {
        documentAnchor[NAME_FIELD_HEIGHT_CONFIRMED] = height;
        AnchorsStored.Update(documentAnchor);

        Console.WriteLine($"Anchor TX {documentAnchor[BlockchainBitcoin.NAME_FIELD_ID].AsBinary.ToHexString()} confirmed at Bitcoin height {height}.");
      }

    int heightPrune = height - BlockchainBitcoin.BITCOIN_REORG_MARGIN;

    OutputsOwnedStored.DeleteMany(Query.Between(nameof(TXOutputOwned.HeightSpent), 1, heightPrune));
    AnchorsStored.DeleteMany(Query.Between(NAME_FIELD_HEIGHT_CONFIRMED, 1, heightPrune));
  }

  void RollBackBlockParent(Block block)
  {
    int height = block.Header.Height;

    HashSet<byte[]> iDsOutputCreated = new(
      OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightCreated), height)).Select(o => o.ID),
      new EqualityComparerByteArray());

    foreach (BsonDocument documentAnchor in AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
      if (ParseAnchor(documentAnchor).Inputs.Any(i => iDsOutputCreated.Contains(TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput))))
        AnchorsStored.Delete(documentAnchor[BlockchainBitcoin.NAME_FIELD_ID]);

    OutputsOwnedStored.DeleteMany(Query.EQ(nameof(TXOutputOwned.HeightCreated), height));

    foreach (TXOutputOwned outputSpent in OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), height)).ToList())
    {
      outputSpent.HeightSpent = 0;
      OutputsOwnedStored.Update(outputSpent);
    }

    foreach (BsonDocument documentAnchor in AnchorsStored.Find(Query.GTE(NAME_FIELD_HEIGHT_CONFIRMED, height)).ToList())
    {
      documentAnchor[NAME_FIELD_HEIGHT_CONFIRMED] = 0;
      AnchorsStored.Update(documentAnchor);
    }
  }

  void HandleParentTip(Block blockParent)
  {
    if (TimerMining == null || blockParent.Header.HeaderNext != null)
      return;

    int heightParentTip = blockParent.Header.Height;

    if (HeightTipParentRound == heightParentTip - 1)
      if (CountAnchorsMissed(DateTime.UtcNow.AddSeconds(-SECONDS_TEMPLATE_REFRESH)) > 0)
      {
        OutcomeAnchorsLast = ", after a missed anchor";
        FeeAnchor = Math.Min(Budget, FeeAnchor + (long)(FEE_PER_BYTE_RAISE_ANCHOR * LENGTH_ANCHOR));
      }
      else
      {
        OutcomeAnchorsLast = ", after the last anchors were mined";
        FeeAnchor = Math.Max(FEE_SURCHARGE_REPLACEMENT_ANCHOR, FeeAnchor - Math.Max(1, (long)(FEE_PER_BYTE_DECREASE_ANCHOR * LENGTH_ANCHOR)));
      }

    int countAnchorsMissed = CountAnchorsMissed(DateTime.MaxValue);

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

    double millisecondsDelayMean = (double)GetCostAnchor(FeeAnchor) * SECONDS_PERIOD_BUDGET * 1000 / Budget;
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

    if (!TryCreateTXAnchor(anchorToken, fee))
      return false;

    block.Serialize();
    Blockchain.StoreBlockAwaitingAnchor(block, BlockchainParent.GetHeight());

    return true;
  }

  TXBitcoin ParseAnchor(BsonDocument documentAnchor)
  {
    byte[] tXRaw = documentAnchor[NAME_FIELD_TX_RAW].AsBinary;
    int index = 0;

    TXBitcoin tX = (TXBitcoin)BlockchainParent.Parser.ParseTX(tXRaw, ref index, isCoinbase: false, verifySignatures: false);

    tX.TXRaw = tXRaw;
    tX.Fee = documentAnchor[NAME_FIELD_FEE].AsInt64;

    return tX;
  }

  void InsertAnchor(TXBitcoin tXAnchor)
  {
    AnchorsStored.Insert(new BsonDocument
    {
      [BlockchainBitcoin.NAME_FIELD_ID] = tXAnchor.Hash,
      [NAME_FIELD_TX_RAW] = tXAnchor.TXRaw,
      [NAME_FIELD_FEE] = tXAnchor.Fee,
      [NAME_FIELD_HEIGHT_CONFIRMED] = 0,
      [NAME_FIELD_TIME_SENT] = DateTime.UtcNow.Ticks
    });
  }

  List<TXBitcoin> LoadAnchorsUnconfirmed()
  {
    return AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
      .Select(ParseAnchor)
      .ToList();
  }

  int CountAnchorsMissed(DateTime timeSentLatest)
  {
    return AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
      .Where(d => d[NAME_FIELD_TIME_SENT].AsInt64 <= timeSentLatest.Ticks)
      .Select(ParseAnchor)
      .Count(t => t.LockTime < BlockchainParent.GetHeight());
  }

  long GetFunds()
  {
    return OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), 0)).Sum(o => o.Value)
      - LoadAnchorsUnconfirmed().Sum(t => t.Fee);
  }

  (TXBitcoin anchorReplaced, List<TXOutputOwned> outputsReplaced) FindAnchorReplaced(List<TXBitcoin> anchorsUnconfirmed, TXOutputTokenAnchor tokenAnchor, int heightTip)
  {
    foreach (TXBitcoin anchor in anchorsUnconfirmed
      .Where(t => t.LockTime != heightTip
        || !t.TXOutputs.OfType<TXOutputTokenAnchor>().Single().HashBlockPreviousReferenced.IsAllBytesEqual(tokenAnchor.HashBlockPreviousReferenced))
      .OrderByDescending(t => t.LockTime == heightTip))
    {
      List<TXOutputOwned> outputsAnchor = anchor.Inputs
        .Select(i => OutputsOwnedStored.FindById(TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput)))
        .ToList();

      if (!outputsAnchor.Contains(null))
        return (anchor, outputsAnchor);
    }

    return (null, []);
  }

  static int GetLengthAnchor(int countInputs, int countOutputsChange)
  {
    return LENGTH_TX_OVERHEAD
      + countInputs * LENGTH_P2PKH_INPUT
      + LENGTH_OUTPUT_OVERHEAD
      + TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN
      + countOutputsChange * LENGTH_P2PKH_OUTPUT;
  }

  static long GetFeeAnchor(long feeAnchor, TXBitcoin anchorReplaced, int countInputs, int countOutputsChange)
  {
    int lengthAnchor = GetLengthAnchor(countInputs, countOutputsChange);

    if (anchorReplaced == null)
      return feeAnchor * lengthAnchor / LENGTH_ANCHOR;

    long feePerLengthAnchor = Math.Max(
      feeAnchor,
      anchorReplaced.Fee * LENGTH_ANCHOR / GetLengthAnchor(anchorReplaced.Inputs.Count, anchorReplaced.TXOutputs.Count - 1) + FEE_SURCHARGE_REPLACEMENT_ANCHOR);

    return Math.Max(
      feePerLengthAnchor * lengthAnchor / LENGTH_ANCHOR,
      anchorReplaced.Fee + FEE_PER_BYTE_INCREMENTAL_RELAY * lengthAnchor);
  }

  void AddOutputsChange(TXBitcoin tXAnchor, long valueSpent, long valueChangeMin, int countOutputsSmall, long valueOutputSmall, Func<int, long> getFee)
  {
    for (int i = 0; i < countOutputsSmall; i++)
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH(valueOutputSmall, BlockchainParent.Wallet.Hash160KeyPublicReceiving));

    long valueChange = valueSpent - countOutputsSmall * valueOutputSmall - getFee(countOutputsSmall + 1);

    if (valueChange >= valueChangeMin)
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH(valueChange, BlockchainParent.Wallet.Hash160KeyPublicReceiving));
  }

  static long GetCostAnchor(long feeAnchor)
  {
    return Math.Max(VALUE_DUST_P2PKH, GetFeeAnchor(feeAnchor, null, 1, 0));
  }

  bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor)
  {
    int heightTip = BlockchainParent.GetHeight();

    List<TXBitcoin> anchorsUnconfirmed = LoadAnchorsUnconfirmed();

    (TXBitcoin anchorReplaced, List<TXOutputOwned> outputsSpent) = FindAnchorReplaced(anchorsUnconfirmed, tokenAnchor, heightTip);

    HashSet<byte[]> iDsOutputOccupied = new(
      anchorsUnconfirmed.SelectMany(t => t.Inputs).Select(i => TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput)),
      new EqualityComparerByteArray());

    List<TXOutputOwned> outputsFree = OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), 0))
      .Where(o => !iDsOutputOccupied.Contains(o.ID))
      .ToList();

    long valueChangeMin = Math.Max(VALUE_DUST_P2PKH, feeAnchor * LENGTH_P2PKH_INPUT / LENGTH_ANCHOR);
    long valueSpent = outputsSpent.Sum(o => o.Value);

    long valueOutputSmall = GetCostAnchor(feeAnchor);
    TXOutputOwned outputReserve = outputsFree.MaxBy(o => o.Value);
    int countOutputsSmallMissing = Math.Max(0, (int)(3 * Budget / valueOutputSmall) - (outputsFree.Count - (outputReserve == null ? 0 : 1)));

    List<TXOutputOwned> outputsCandidate = outputsFree.OrderBy(o => o.Value).ToList();

    if (anchorReplaced == null && countOutputsSmallMissing > 0 && outputReserve != null)
    {
      outputsCandidate.Remove(outputReserve);
      outputsSpent.Add(outputReserve);
      valueSpent += outputReserve.Value;
    }

    while (valueSpent < GetFeeAnchor(feeAnchor, anchorReplaced, outputsSpent.Count, 0))
    {
      if (outputsCandidate.Count == 0 || outputsSpent.Count == COUNT_INPUTS_MAX)
        return false;

      TXOutputOwned outputAdded = outputsCandidate.FirstOrDefault(o => valueSpent + o.Value >= GetFeeAnchor(feeAnchor, anchorReplaced, outputsSpent.Count + 1, 0))
        ?? outputsCandidate[0];

      outputsCandidate.Remove(outputAdded);
      outputsSpent.Add(outputAdded);
      valueSpent += outputAdded.Value;
    }

    TXBitcoin tXAnchor = new()
    {
      LockTime = heightTip
    };

    foreach (TXOutputOwned outputSpent in outputsSpent)
      tXAnchor.Inputs.Add(new TXInputBitcoin
      {
        TXIDOutput = outputSpent.TXID,
        IndexOutput = outputSpent.Index
      });

    tokenAnchor.Serialize();
    tXAnchor.TXOutputs.Add(tokenAnchor);

    int countOutputsSmall = 0;

    while (countOutputsSmall < countOutputsSmallMissing
      && valueSpent - (countOutputsSmall + 1) * valueOutputSmall - GetFeeAnchor(feeAnchor, anchorReplaced, outputsSpent.Count, countOutputsSmall + 2) >= valueChangeMin)
      countOutputsSmall++;

    AddOutputsChange(
      tXAnchor,
      valueSpent,
      valueChangeMin,
      countOutputsSmall,
      valueOutputSmall,
      countOutputsChange => GetFeeAnchor(feeAnchor, anchorReplaced, outputsSpent.Count, countOutputsChange));

    tXAnchor.Serialize(BlockchainParent.Wallet, outputsSpent.Select(o => o.IDAccount).ToList());

    tXAnchor.Fee = valueSpent - tXAnchor.GetValueOutputs();

    NetworkParent.SendTX(tXAnchor.TXRaw);

    if (anchorReplaced != null)
      AnchorsStored.Delete(anchorReplaced.Hash);

    InsertAnchor(tXAnchor);

    Console.WriteLine(
      $"Anchor TX {tXAnchor.Hash.ToHexString()} with fee {tXAnchor.Fee} referencing BToken block {tokenAnchor.HashBlockReferenced.ToHexString()} spends {string.Join(", ", outputsSpent.Select(o => $"output {o.Index} of {o.TXID.ToHexString()}"))}" +
      (anchorReplaced != null ? $" and replaces anchor TX {anchorReplaced.Hash.ToHexString()}." : "."));

    return true;
  }

  class TXOutputOwned
  {
    [BsonId]
    internal byte[] ID;

    [BsonField]
    internal byte[] TXID;

    [BsonField]
    internal int Index;

    [BsonField]
    internal byte[] IDAccount;

    [BsonField]
    internal long Value;

    [BsonField]
    internal int HeightCreated;

    [BsonField]
    internal int HeightSpent;


    internal static byte[] CreateID(byte[] tXID, int index)
    {
      return [.. tXID, .. BitConverter.GetBytes(index)];
    }
  }
}
