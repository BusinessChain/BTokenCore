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
  internal const int LENGTH_OUTPUT_ANCHOR =LENGTH_OUTPUT_OVERHEAD + TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN;

  const long FEE_PER_BYTE_INCREMENTAL_RELAY = 1;
  internal const long FEE_SURCHARGE_REPLACEMENT_ANCHOR = FEE_PER_BYTE_INCREMENTAL_RELAY * LENGTH_ANCHOR;

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

  IEnumerable<(TXBitcoin anchorReplaced, TXOutputOwned outputReplaced, long feeReplacement)> EnumerateAnchorsReplaceable(List<TXBitcoin> anchorsUnconfirmed, long feeAnchor, int lengthReplacement)
  {
    foreach (TXBitcoin anchorReplaced in anchorsUnconfirmed)
    {
      TXInputBitcoin inputReplaced = anchorReplaced.Inputs[0];
      long feeReplacement = Math.Max(feeAnchor, anchorReplaced.Fee + FEE_PER_BYTE_INCREMENTAL_RELAY * lengthReplacement);

      if (OutputsOwnedStored.FindById(TXOutputOwned.CreateID(inputReplaced.TXIDOutput, inputReplaced.IndexOutput)) is TXOutputOwned outputReplaced
        && outputReplaced.Value >= feeReplacement)
        yield return (anchorReplaced, outputReplaced, feeReplacement);
    }
  }

  internal override bool HasAnchorMissed()
  {
    return LoadAnchorsUnconfirmed().Any(t => t.LockTime < ChainRoot.HeaderTipBlockchain.Height);
  }

  protected override long? GetBalanceWallet()
  {
    return OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), 0)).Sum(o => o.Value)
      - LoadAnchorsUnconfirmed().Sum(t => t.Fee);
  }

  internal override bool TryCreateTXAnchor(List<TXOutputTokenAnchor> tokenAnchors, long feeAnchor, out int lockTime)
  {
    int heightTip = ChainRoot.HeaderTipBlockchain.Height;
    lockTime = heightTip;

    int lengthTXAnchor = LENGTH_ANCHOR + (tokenAnchors.Count - 1) * LENGTH_OUTPUT_ANCHOR;

    List<TXBitcoin> anchorsUnconfirmed = LoadAnchorsUnconfirmed();

    (TXBitcoin anchorReplaced, TXOutputOwned outputSpent, long feeTX) = EnumerateAnchorsReplaceable(anchorsUnconfirmed, feeAnchor, lengthTXAnchor)
      .FirstOrDefault((null, null, feeAnchor));

    if (outputSpent == null)
    {
      HashSet<byte[]> iDsOutputOccupied = new(
        anchorsUnconfirmed.SelectMany(t => t.Inputs).Select(i => TXOutputOwned.CreateID(i.TXIDOutput, i.IndexOutput)),
        new EqualityComparerByteArray());

      outputSpent = OutputsOwnedStored.Find(Query.EQ(nameof(TXOutputOwned.HeightSpent), 0))
        .FirstOrDefault(o => o.Value >= feeTX && !iDsOutputOccupied.Contains(o.ID));

      if (outputSpent == null)
        return false;
    }

    TXBitcoin tXAnchor = new()
    {
      LockTime = heightTip
    };

    tXAnchor.Inputs.Add(new TXInputBitcoin
    {
      TXIDOutput = outputSpent.TXID,
      IndexOutput = outputSpent.Index
    });

    foreach (TXOutputTokenAnchor tokenAnchor in tokenAnchors)
    {
      tokenAnchor.Serialize();
      tXAnchor.TXOutputs.Add(tokenAnchor);
    }

    long valueChange = outputSpent.Value - feeTX;

    if (valueChange >= feeAnchor * LENGTH_P2PKH_INPUT / lengthTXAnchor)
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH(valueChange, Wallet.Hash160KeyPublicReceiving));

    tXAnchor.Serialize(Wallet, outputSpent.IDAccount);

    tXAnchor.Fee = outputSpent.Value - tXAnchor.GetValueOutputs();

    if (anchorReplaced != null)
      TXsOwnedStored.Delete(anchorReplaced.Hash);

    InsertTXOwned(tXAnchor);

    return true;
  }
}
