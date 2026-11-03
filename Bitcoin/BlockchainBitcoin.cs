using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const string NAME_COLLECTION_OUTPUTS_OWNED = "outputsOwned";

  const int LENGTH_P2PKH_INPUT = 148;
  const int LENGTH_P2PKH_OUTPUT = 34;
  const int LENGTH_TX_OVERHEAD = 10;

  const long FEE_PER_BYTE_INCREMENTAL_RELAY = 1;

  ILiteCollection<TXOutputOwned> OutputsOwned;


  internal BlockchainBitcoin(SemaphoreSlim semaphoreBlockchain)
    : base("TokenBitcoin", blockchainParent: null, semaphoreBlockchain)
  {
    SizeBlockMax = SIZE_BLOCK_MAX;

    OutputsOwned = Database.GetCollection<TXOutputOwned>(NAME_COLLECTION_OUTPUTS_OWNED);
  }

  protected override Header CreateHeaderGenesis()
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

  internal override Header ParseHeader(byte[] buffer, ref int index)
  {
    return new HeaderBitcoin(buffer, ref index);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool flagIsCoinbase)
  {
    return new TXBitcoin(buffer, ref index, flagIsCoinbase);
  }

  protected override void ParseBlockStored(Block block)
  {
    int startIndex = ParseBlockHeader(block);

    int countAnchors = VarInt.GetInt(block.Buffer, ref startIndex);

    for (int i = 0; i < countAnchors; i++)
    {
      TXOutputTokenAnchor anchor = new(block.Buffer, ref startIndex);
      block.Header.AnchorsWinner.TryAdd(anchor.IDToken, anchor);
    }
  }

  protected override byte[] SerializeBlockStored(Block block)
  {
    List<byte> bytes = [.. block.Header.Serialize(), .. VarInt.GetBytes(block.Header.AnchorsWinner.Count)];

    foreach (TXOutputTokenAnchor anchor in block.Header.AnchorsWinner.Values)
    {
      anchor.Serialize();
      bytes.AddRange(anchor.Script);
    }

    return bytes.ToArray();
  }

  protected override void InsertBlock(Block block)
  {
    int height = block.Header.Height;

    int heightPruning = height - MAX_COUNT_BLOCKS_STORED;

    OutputsOwned.DeleteMany(Query.Between("HeightSpent", 1, heightPruning));

    foreach (TXBitcoin tX in block.TXs)
    {
      foreach (TXInputBitcoin tXInput in tX.Inputs)
        if (OutputsOwned.FindById(TXOutputOwned.CreateID(tXInput.TXIDOutput, tXInput.OutputIndex)) is TXOutputOwned outputSpent)
        {
          outputSpent.HeightSpent = height;
          OutputsOwned.Update(outputSpent);
        }

      for (int i = 0; i < tX.TXOutputs.Count; i++)
        if (tX.TXOutputs[i] is TXOutputP2PKH tXOutput &&
          tXOutput.IDAccount.IsAllBytesEqual(Wallet.Hash160PKeyPublic))
          OutputsOwned.Insert(new TXOutputOwned
          {
            ID = TXOutputOwned.CreateID(tX.Hash, i),
            TXID = tX.Hash,
            Index = i,
            Value = tXOutput.Value,
            HeightCreated = height
          });
    }
  }

  protected override void RollBackBlock(Block block)
  {
    int height = block.Header.Height;

    OutputsOwned.DeleteMany(Query.EQ("HeightCreated", height));

    foreach (TXOutputOwned outputSpent in OutputsOwned.Find(Query.EQ("HeightSpent", height)).ToList())
    {
      outputSpent.HeightSpent = 0;
      OutputsOwned.Update(outputSpent);
    }

    if (TXsOwned.Exists(Query.EQ("heightConfirmed", height)))
      TXsOwned.DeleteMany(Query.EQ("heightConfirmed", 0));
  }

  protected override bool IsTXOwnedConfirmed(TX tX, Block block)
  {
    List<TXInputBitcoin> inputsTXOwned = ((TXBitcoin)tX).Inputs;

    return block.TXs.Any(t => ((TXBitcoin)t).Inputs.Any(tXInput =>
      inputsTXOwned.Any(i => i.TXIDOutput.IsAllBytesEqual(tXInput.TXIDOutput) && i.OutputIndex == tXInput.OutputIndex)));
  }

  internal bool IsTXAnchorOpen()
  {
    return TXsOwned.Exists(Query.EQ("heightConfirmed", 0));
  }

  internal bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte)
  {
    TXBitcoin tXAnchor = new();

    TXBitcoin tXAnchorOpen = null;

    if (TXsOwned.FindOne(Query.EQ("heightConfirmed", 0)) is BsonDocument documentAnchorOpen)
      tXAnchorOpen = (TXBitcoin)ParseTXOwned(documentAnchorOpen);

    long feePerInputP2PKH = (LENGTH_P2PKH_INPUT * feePerByte);
    long feePerOutputP2PKH = (LENGTH_P2PKH_OUTPUT * feePerByte);

    List<TXOutputOwned> outputsSpendable;

    if (tXAnchorOpen != null)
      outputsSpendable = tXAnchorOpen.Inputs
        .Select(i => OutputsOwned.FindById(TXOutputOwned.CreateID(i.TXIDOutput, i.OutputIndex)))
        .Where(o => o != null)
        .ToList();
    else
      outputsSpendable = OutputsOwned.Find(Query.EQ("HeightSpent", 0))
        .Where(o => o.Value > feePerInputP2PKH)
        .Take(VarInt.PREFIX_UINT16 - 1).ToList();

    long valueInputs = outputsSpendable.Sum(o => o.Value);

    tokenAnchor.Serialize();

    long lengthTX = LENGTH_P2PKH_INPUT * outputsSpendable.Count
      + LENGTH_TX_OVERHEAD
      + tokenAnchor.Script.Length;

    long feeTX = feePerByte * lengthTX;

    if (tXAnchorOpen != null)
      feeTX = Math.Max(
        feeTX,
        tXAnchorOpen.Fee + FEE_PER_BYTE_INCREMENTAL_RELAY * (lengthTX + LENGTH_P2PKH_OUTPUT));

    if (valueInputs < feeTX)
      return false;

    long valueChange = valueInputs - feeTX - feePerOutputP2PKH;

    bool flagCreateOutputChange = valueChange > feePerInputP2PKH;

    foreach (TXOutputOwned outputSpendable in outputsSpendable)
    {
      tXAnchor.Inputs.Add(new TXInputBitcoin
      {
        TXIDOutput = outputSpendable.TXID,
        OutputIndex = outputSpendable.Index
      });
    }

    tXAnchor.TXOutputs.Add(tokenAnchor);

    if (flagCreateOutputChange)
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH(valueChange, Wallet.Hash160PKeyPublic));

    tXAnchor.Serialize(Wallet);

    tXAnchor.Fee = valueInputs - tXAnchor.GetValueOutputs();

    if (tXAnchorOpen != null)
      TXsOwned.Delete(tXAnchorOpen.Hash);

    InsertTXOwned(tXAnchor);

    return true;
  }
}
