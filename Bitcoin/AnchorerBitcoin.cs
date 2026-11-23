using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBitcoin
{
  internal class AnchorerBitcoin : Anchorer
  {
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

    ILiteCollection<TXOutputOwned> OutputsOwnedStored;
    HashSet<byte[]> IDsOutputWithdrawn = new(new EqualityComparerByteArray());


    internal AnchorerBitcoin(BlockchainBitcoin blockchain, NetworkBitcoin network, LiteDatabase liteDatabase)
      : base(blockchain, network, liteDatabase)
    {
      OutputsOwnedStored = liteDatabase.GetCollection<TXOutputOwned>("outputsOwned");
    }

    protected override void InsertBlockParentFunds(Block block)
    {
      int height = block.Header.Height;

      foreach (TXBitcoin tX in block.TXs)
      {
        foreach (TXInputBitcoin tXInput in tX.Inputs)
          if (OutputsOwnedStored.FindById(TXOutputOwned.CreateID(tXInput.TXIDOutput, tXInput.IndexOutput)) is TXOutputOwned outputSpent)
          {
            outputSpent.HeightSpent = height;
            OutputsOwnedStored.Update(outputSpent);
            IDsOutputWithdrawn.Remove(outputSpent.ID);
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

      OutputsOwnedStored.DeleteMany(Query.Between(nameof(TXOutputOwned.HeightSpent), 1, height - BITCOIN_REORG_MARGIN));
    }

    protected override bool IsAnchorConflicting(TX anchor, int height)
    {
      return ((TXBitcoin)anchor).Inputs.Any(i =>
        OutputsOwnedStored.FindById(TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput)) is TXOutputOwned output
        && output.HeightSpent == height);
    }

    protected override void RollBackBlockParentFunds(Block block)
    {
      int height = block.Header.Height;

      HashSet<byte[]> iDsOutputCreated = new(
        OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightCreated), height)).Select(o => o.ID),
        new EqualityComparerByteArray());

      foreach (TXBitcoin anchor in LoadAnchorsUnconfirmed().Cast<TXBitcoin>())
        if (anchor.Inputs.Any(i => iDsOutputCreated.Contains(TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput))))
          AnchorsStored.Delete(anchor.Hash);

      OutputsOwnedStored.DeleteMany(Query.EQ(nameof(TXOutputOwned.HeightCreated), height));

      foreach (TXOutputOwned outputSpent in OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), height)).ToList())
      {
        outputSpent.HeightSpent = 0;
        OutputsOwnedStored.Update(outputSpent);
      }
    }

    protected override bool IsAnchorMissed(TX anchor)
    {
      return anchor.LockTime < Blockchain.GetHeight();
    }

    internal override long GetFunds()
    {
      return OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), 0)).Where(o => !IDsOutputWithdrawn.Contains(o.ID)).Sum(o => o.Value)
        - LoadAnchorsUnconfirmed().Sum(t => t.Fee);
    }

    List<TXOutputOwned> LoadOutputsFree(List<TXBitcoin> anchorsUnconfirmed)
    {
      HashSet<byte[]> iDsOutputOccupied = new(
        anchorsUnconfirmed.SelectMany(t => t.Inputs).Select(i => TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput)),
        new EqualityComparerByteArray());

      iDsOutputOccupied.UnionWith(IDsOutputWithdrawn);

      return OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), 0))
        .Where(o => !iDsOutputOccupied.Contains(o.ID))
        .ToList();
    }

    static TXBitcoin CreateTX(List<TXOutputOwned> outputsSpent, int lockTime)
    {
      TXBitcoin tX = new()
      {
        LockTime = lockTime
      };

      foreach (TXOutputOwned outputSpent in outputsSpent)
        tX.Inputs.Add(new TXInputBitcoin
        {
          TXIDOutput = outputSpent.TXID,
          IndexOutput = outputSpent.Index
        });

      return tX;
    }

    void SendTX(TXBitcoin tX, List<TXOutputOwned> outputsSpent)
    {
      tX.Fee = outputsSpent.Sum(o => o.Value) - tX.GetValueOutputs();

      if (tX.Fee < 0)
        throw new ProtocolException($"The outputs of the transaction exceed its inputs by {-tX.Fee} satoshis.");

      tX.Serialize(Wallet, outputsSpent.Select(o => o.IDAccount).ToList());

      Network.SendTX(tX.TXRaw);
    }

    internal TXBitcoin CreateTXWithdrawal(byte[] iDAccountTarget, long fee)
    {
      List<TXOutputOwned> outputsSpent = LoadOutputsFree(LoadAnchorsUnconfirmed().Cast<TXBitcoin>().ToList()).OrderByDescending(o => o.Value).Take(COUNT_INPUTS_MAX).ToList();

      long value = outputsSpent.Sum(o => o.Value) - fee;

      if (value < VALUE_DUST_P2PKH)
        throw new ProtocolException($"The free funds minus the fee {fee} do not reach the dust limit of {VALUE_DUST_P2PKH} satoshis.", "fee");

      TXBitcoin tXWithdrawal = CreateTX(outputsSpent, 0);

      tXWithdrawal.TXOutputs.Add(new TXOutputP2PKH(value, iDAccountTarget));

      SendTX(tXWithdrawal, outputsSpent);

      outputsSpent.ForEach(o => IDsOutputWithdrawn.Add(o.ID));

      Console.WriteLine($"Withdrawal TX {tXWithdrawal.Hash.ToHexString()} with fee {tXWithdrawal.Fee} sends {value} to {iDAccountTarget.BinaryToBase58Check()}.");

      return tXWithdrawal;
    }

    internal override long GetFeeAnchorMin()
    {
      return FEE_SURCHARGE_REPLACEMENT_ANCHOR;
    }

    internal override long GetStepFeeRaise()
    {
      return (long)(FEE_PER_BYTE_RAISE_ANCHOR * LENGTH_ANCHOR);
    }

    internal override long GetStepFeeDecrease()
    {
      return Math.Max(1, (long)(FEE_PER_BYTE_DECREASE_ANCHOR * LENGTH_ANCHOR));
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

    internal override long GetCostAnchor(long feeAnchor)
    {
      return Math.Max(VALUE_DUST_P2PKH, GetFeeAnchor(feeAnchor, null, 1, 0));
    }

    internal override bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor, long budget)
    {
      int heightTip = Blockchain.GetHeight();

      List<TXBitcoin> anchorsUnconfirmed = LoadAnchorsUnconfirmed().Cast<TXBitcoin>().ToList();

      (TXBitcoin anchorReplaced, List<TXOutputOwned> outputsSpent) = FindAnchorReplaced(anchorsUnconfirmed, tokenAnchor, heightTip);

      List<TXOutputOwned> outputsFree = LoadOutputsFree(anchorsUnconfirmed);

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

      TXBitcoin tXAnchor = CreateTX(outputsSpent, heightTip);

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

      SendTX(tXAnchor, outputsSpent);

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
}
