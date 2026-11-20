using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  internal const string NAME_TOKEN = "TokenBitcoin";

  const string NAME_COLLECTION_OUTPUTS_OWNED = "outputsOwned";

  const int LENGTH_P2PKH_INPUT = 148;
  const int LENGTH_P2PKH_OUTPUT = 34;
  const int LENGTH_TX_OVERHEAD = 10;
  const int LENGTH_OUTPUT_OVERHEAD = 9;
  internal const int LENGTH_ANCHOR = LENGTH_TX_OVERHEAD
    + LENGTH_P2PKH_INPUT
    + LENGTH_OUTPUT_OVERHEAD
    + TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN
    + LENGTH_P2PKH_OUTPUT;

  const long FEE_PER_BYTE_INCREMENTAL_RELAY = 1;
  internal const long FEE_SURCHARGE_REPLACEMENT_ANCHOR = FEE_PER_BYTE_INCREMENTAL_RELAY * LENGTH_ANCHOR;

  const long VALUE_DUST_P2PKH = 546;

  const int COUNT_INPUTS_MAX = 252;

  protected ILiteCollection<TXOutputOwned> OutputsOwnedStored;


  internal BlockchainBitcoin(LiteDatabase liteDatabase, SemaphoreSlim semaphoreBlockchain)
    : base(NAME_TOKEN, liteDatabase, SIZE_BLOCK_MAX, new ParserBitcoin(), blockchainParent: null, semaphoreBlockchain)
  {
    OutputsOwnedStored = LiteDatabase.GetCollection<TXOutputOwned>(NAME_COLLECTION_OUTPUTS_OWNED);
  }

  protected override Header CreateHeaderRoot()
  {
    HeaderBitcoin header = new(
       headerHash: "000000000000000000001e39df127cbab82a824ac43cfcdbf62e59f6a08b9f0b".ToBinary(),
       version: 0x2000e000,
       hashPrevious: "00000000000000000001438062096e4055a0de69e2a56a3b69d3be5a140974ea".ToBinary(),
       merkleRootHash: "933a6ea902765147f88538292bb11ef0afc77fe368c3fd1845aaf368c40cf4a8".ToBinary(),
       unixTimeSeconds: 1791011796,
       nBits: 386014960,
       nonce: 161796985);

    header.Height = 969696;

    return header;
  }

  protected override void InsertBlock(Block block)
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
        if (tX.TXOutputs[i] is TXOutputP2PKH tXOutput && Wallet.IsKeyOwned(tXOutput.IDAccount))
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

    int heightPrune = height - BITCOIN_REORG_MARGIN;

    OutputsOwnedStored.DeleteMany(Query.Between(nameof(TXOutputOwned.HeightSpent), 1, heightPrune));
    TXsOwnedStored.DeleteMany(Query.Between(NAME_FIELD_HEIGHT_CONFIRMED, 1, heightPrune));
  }

  protected override void RollBackBlock(Block block)
  {
    int height = block.Header.Height;

    HashSet<byte[]> iDsOutputCreated = new(
      OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightCreated), height)).Select(o => o.ID),
      new EqualityComparerByteArray());

    foreach (BsonDocument documentTXOwned in TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
      if (((TXBitcoin)ParseTXOwned(documentTXOwned)).Inputs.Any(i => iDsOutputCreated.Contains(TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput))))
        TXsOwnedStored.Delete(documentTXOwned[NAME_FIELD_ID]);

    OutputsOwnedStored.DeleteMany(Query.EQ(nameof(TXOutputOwned.HeightCreated), height));

    foreach (TXOutputOwned outputSpent in OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), height)).ToList())
    {
      outputSpent.HeightSpent = 0;
      OutputsOwnedStored.Update(outputSpent);
    }
  }

  protected override bool IsTXOwnedConfirmed(TX tX, Block block)
  {
    return ((TXBitcoin)tX).Inputs.Any(i =>
      OutputsOwnedStored.FindById(TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput)) is TXOutputOwned output
      && output.HeightSpent == block.Header.Height);
  }

  List<TXBitcoin> LoadAnchorsUnconfirmed()
  {
    return TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
      .Select(d => (TXBitcoin)ParseTXOwned(d))
      .ToList();
  }

  internal override List<byte[]> GetHashesTXOwnedUnconfirmed()
  {
    if (!IsBlockTipAtHeaderTip())
      return [];

    return LoadAnchorsUnconfirmed()
      .Where(t => t.LockTime == ChainRoot.HeaderTipBlockchain.Height)
      .Select(t => t.Hash)
      .ToList();
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
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH(valueOutputSmall, Wallet.Hash160KeyPublicReceiving));

    long valueChange = valueSpent - countOutputsSmall * valueOutputSmall - getFee(countOutputsSmall + 1);

    if (valueChange >= valueChangeMin)
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH(valueChange, Wallet.Hash160KeyPublicReceiving));
  }

  internal long GetSecondsAgeHeaderTip()
  {
    return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ((HeaderBitcoin)ChainRoot.HeaderTip).UnixTimeSeconds;
  }

  internal override long GetCostAnchor(long feeAnchor)
  {
    return Math.Max(VALUE_DUST_P2PKH, GetFeeAnchor(feeAnchor, null, 1, 0));
  }

  internal override int CountAnchorsMissed(DateTime timeSentLatest)
  {
    return TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
      .Where(d => IsTXOwnedSentUntil(d, timeSentLatest))
      .Select(ParseTXOwned)
      .Count(t => t.LockTime < ChainRoot.HeaderTipBlockchain.Height);
  }

  protected override long? GetBalanceWallet()
  {
    return OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), 0)).Sum(o => o.Value)
      - LoadAnchorsUnconfirmed().Sum(t => t.Fee);
  }

  internal override bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor, long budget)
  {
    int heightTip = ChainRoot.HeaderTipBlockchain.Height;

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
    int countOutputsSmallMissing = Math.Max(0, (int)(3 * budget / valueOutputSmall) - (outputsFree.Count - (outputReserve == null ? 0 : 1)));

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

    tXAnchor.Serialize(Wallet, outputsSpent.Select(o => o.IDAccount).ToList());

    tXAnchor.Fee = valueSpent - tXAnchor.GetValueOutputs();

    if (anchorReplaced != null)
      TXsOwnedStored.Delete(anchorReplaced.Hash);

    InsertTXOwned(tXAnchor);

    Console.WriteLine(
      $"Anchor TX {tXAnchor.Hash.ToHexString()} with fee {tXAnchor.Fee} referencing BToken block {tokenAnchor.HashBlockReferenced.ToHexString()} spends {string.Join(", ", outputsSpent.Select(o => $"output {o.Index} of {o.TXID.ToHexString()}"))}" +
      (anchorReplaced != null ? $" and replaces anchor TX {anchorReplaced.Hash.ToHexString()}." : "."));

    return true;
  }
}
