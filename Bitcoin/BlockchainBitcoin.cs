using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const string NAME_COLLECTION_OUTPUTS_OWNED = "outputsOwned";

  const int LENGTH_P2PKH_INPUT = 148;
  const int LENGTH_P2PKH_OUTPUT = 34;
  const int LENGTH_TX_OVERHEAD = 10;

  const long FEE_PER_BYTE_INCREMENTAL_RELAY = 1;

  ILiteCollection<TXOutputOwned> OutputsOwnedStored;


  internal BlockchainBitcoin(SemaphoreSlim semaphoreBlockchain)
    : base("TokenBitcoin", SIZE_BLOCK_MAX, blockchainParent: null, semaphoreBlockchain)
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

  internal override Header ParseHeader(byte[] buffer, ref int index)
  {
    return new HeaderBitcoin(buffer, ref index);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures)
  {
    return new TXBitcoin(buffer, ref index, isCoinbase);
  }

  protected override void DrawAnchorsWinner(Block block)
  {
    Dictionary<byte[], byte[]> scoresAnchorWinner = new(new EqualityComparerByteArray());

    foreach (TXBitcoin tX in block.TXs)
    {
      if (tX.LockTime != block.Header.Height - 1)
        continue;

      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput is TXOutputTokenAnchor tokenAnchor)
        {
          byte[] scoreAnchor = SHA256.HashData([.. block.Header.Hash, .. tokenAnchor.HashBlockReferenced, .. tokenAnchor.HashBlockPreviousReferenced]);

          if (!scoresAnchorWinner.TryGetValue(tokenAnchor.IDToken, out byte[] scoreAnchorWinner)
            || scoreAnchor.IsLessThan(scoreAnchorWinner))
          {
            scoresAnchorWinner[tokenAnchor.IDToken] = scoreAnchor;
            block.Header.AnchorsWinner[tokenAnchor.IDToken] = tokenAnchor;
          }
        }
    }
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

    foreach (TXBitcoin tX in block.TXs)
    {
      foreach (TXInputBitcoin tXInput in tX.Inputs)
        if (OutputsOwnedStored.FindById(TXOutputOwned.CreateID(tXInput.TXIDOutput, tXInput.IndexOutput)) is TXOutputOwned outputSpent)
        {
          outputSpent.HeightSpent = height;
          OutputsOwnedStored.Update(outputSpent);
        }

      for (int i = 0; i < tX.TXOutputs.Count; i++)
        if (tX.TXOutputs[i] is TXOutputP2PKH tXOutput &&
          tXOutput.IDAccount.IsAllBytesEqual(Wallet.Hash160PKeyPublic))
          OutputsOwnedStored.Insert(new TXOutputOwned
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
    List<TXInputBitcoin> inputsTXOwned = ((TXBitcoin)tX).Inputs;

    return block.TXs.Any(t => ((TXBitcoin)t).Inputs.Any(tXInput =>
      inputsTXOwned.Any(i => i.TXIDOutput.IsAllBytesEqual(tXInput.TXIDOutput) && i.IndexOutput == tXInput.IndexOutput)));
  }

  internal override bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor)
  {
    int heightTip = ChainRoot.HeaderTipBlockchain.Height;

    List<TXBitcoin> anchorsUnconfirmed = TXsOwnedStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
      .Select(d => (TXBitcoin)ParseTXOwned(d))
      .ToList();

    tokenAnchor.Serialize();

    long lengthTX = LENGTH_TX_OVERHEAD
      + LENGTH_P2PKH_INPUT
      + tokenAnchor.Script.Length
      + LENGTH_P2PKH_OUTPUT;

    TXBitcoin anchorReplaced = null;
    TXOutputOwned outputSpent = null;
    long feeTX = feeAnchor;

    foreach (TXBitcoin anchorMissed in anchorsUnconfirmed.Where(t => t.LockTime < heightTip))
    {
      TXInputBitcoin inputMissed = anchorMissed.Inputs[0];
      long feeReplacement = Math.Max(feeAnchor, anchorMissed.Fee + FEE_PER_BYTE_INCREMENTAL_RELAY * lengthTX);

      if (OutputsOwnedStored.FindById(TXOutputOwned.CreateID(inputMissed.TXIDOutput, inputMissed.IndexOutput)) is TXOutputOwned outputMissed
        && outputMissed.Value >= feeReplacement)
      {
        anchorReplaced = anchorMissed;
        outputSpent = outputMissed;
        feeTX = feeReplacement;
        break;
      }
    }

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

    tXAnchor.TXOutputs.Add(tokenAnchor);

    long valueChange = outputSpent.Value - feeTX;

    if (valueChange >= feeAnchor * LENGTH_P2PKH_INPUT / lengthTX)
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH(valueChange, Wallet.Hash160PKeyPublic));

    tXAnchor.Serialize(Wallet);

    tXAnchor.Fee = outputSpent.Value - tXAnchor.GetValueOutputs();

    if (anchorReplaced != null)
      TXsOwnedStored.Delete(anchorReplaced.Hash);

    InsertTXOwned(tXAnchor);

    return true;
  }
}
