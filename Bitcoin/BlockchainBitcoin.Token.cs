using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const string NAME_COLLECTION_OUTPUTS_OWNED = "outputsOwned";
  const string NAME_COLLECTION_ANCHORS_OWNED = "anchorsOwned";

  const int LENGTH_P2PKH_INPUT = 148;
  const int LENGTH_P2PKH_OUTPUT = 34;
  const int LENGTH_TX_OVERHEAD = 10;

  const long FEE_PER_BYTE_INCREMENTAL_RELAY = 1;

  ILiteCollection<TXOutputOwned> OutputsOwned;
  ILiteCollection<BsonDocument> AnchorsOwned;
  TXBitcoin TXAnchorOpen;


  internal bool IsTXAnchorOpen()
  {
    return TXAnchorOpen != null;
  }

  void LoadTXAnchorOpen(BsonDocument documentAnchor)
  {
    byte[] tXRaw = documentAnchor["tXRaw"].AsBinary;
    int index = 0;

    TXAnchorOpen = new TXBitcoin(tXRaw, ref index, Wallet.SHA256, flagIsCoinbase: false)
    {
      TXRaw = tXRaw,
      Fee = documentAnchor["fee"].AsInt64
    };
  }

  protected override Header CreateHeaderGenesis()
  {
    HeaderBitcoin header = new(
       headerHash: "000000000000000000010538edbfd2d5b809a33dd83f284aeea41c6d0d96968a".ToBinary(),
       version: 0x20aba000,
       hashPrevious: "0000000000000000000196400396be46d0816dc462df4c3450972f589f4d7d24".ToBinary(),
       merkleRootHash: "0cfb54e522b07bd1a381adc774ec1851590ef4c3add83958135106534569f970".ToBinary(),
       unixTimeSeconds: 1749188499,
       nBits: 386021236,
       nonce: 2455752826);

    header.Height = 900000;

    return header;
  }

  internal override Header ParseHeader(byte[] buffer, ref int index, SHA256 sHA256)
  {
    byte[] hash = sHA256.ComputeHash(
        sHA256.ComputeHash(
          buffer,
          index,
          HeaderBitcoin.COUNT_HEADER_BYTES));

    uint version = BitConverter.ToUInt32(buffer, index);
    index += 4;

    byte[] hashHeaderPrevious = new byte[32];
    Array.Copy(buffer, index, hashHeaderPrevious, 0, 32);
    index += 32;

    byte[] merkleRootHash = new byte[32];
    Array.Copy(buffer, index, merkleRootHash, 0, 32);
    index += 32;

    uint unixTimeSeconds = BitConverter.ToUInt32(buffer, index);
    index += 4;

    bool isBlockTimePremature = unixTimeSeconds >
      (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 * 60 * 60);

    if (isBlockTimePremature)
      throw new ProtocolException($"Timestamp premature {new DateTime(unixTimeSeconds).Date}.");

    uint nBits = BitConverter.ToUInt32(buffer, index);
    index += 4;

    if (hash.IsGreaterThan(nBits))
      throw new ProtocolException($"Header hash {hash.ToHexString()} greater than NBits {nBits}.");

    uint nonce = BitConverter.ToUInt32(buffer, index);
    index += 4;

    return new HeaderBitcoin(
      hash,
      version,
      hashHeaderPrevious,
      merkleRootHash,
      unixTimeSeconds,
      nBits,
      nonce);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
  {
    return new TXBitcoin(buffer, ref index, sHA256, flagIsCoinbase);
  }

  internal bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte, out TX tXAnchor)
  {
    tXAnchor = new TXBitcoin();

    long feePerInputP2PKH = (LENGTH_P2PKH_INPUT * feePerByte);
    long feePerOutputP2PKH = (LENGTH_P2PKH_OUTPUT * feePerByte);

    List<TXOutputOwned> outputsSpendable;

    if (TXAnchorOpen != null)
      outputsSpendable = TXAnchorOpen.Inputs
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

    if (TXAnchorOpen != null)
      feeTX = Math.Max(
        feeTX,
        TXAnchorOpen.Fee + FEE_PER_BYTE_INCREMENTAL_RELAY * (lengthTX + LENGTH_P2PKH_OUTPUT));

    if (valueInputs < feeTX + tokenAnchor.Value)
      return false;

    long valueChange = valueInputs - tokenAnchor.Value - feeTX - feePerOutputP2PKH;

    bool flagCreateOutputChange = valueChange > feePerInputP2PKH;

    foreach (TXOutputOwned outputSpendable in outputsSpendable)
    {
      ((TXBitcoin)tXAnchor).Inputs.Add(new TXInputBitcoin
      {
        TXIDOutput = outputSpendable.TXID,
        OutputIndex = outputSpendable.Index
      });
    }

    tXAnchor.TXOutputs.Add(tokenAnchor);

    if (flagCreateOutputChange)
      tXAnchor.TXOutputs.Add(new TXOutputP2PKH
      {
        Value = valueChange,
        IDAccount = Wallet.Hash160PKeyPublic,
        Script = [.. TXOutputP2PKH.PREFIX_P2PKH, .. Wallet.Hash160PKeyPublic, .. TXOutputP2PKH.POSTFIX_P2PKH]
      });

    tXAnchor.Serialize(Wallet);

    tXAnchor.Fee = valueInputs - tXAnchor.GetValueOutputs();

    if (TXAnchorOpen != null)
      AnchorsOwned.Delete(TXAnchorOpen.Hash);

    AnchorsOwned.Insert(new BsonDocument
    {
      ["_id"] = tXAnchor.Hash,
      ["tXRaw"] = tXAnchor.TXRaw,
      ["fee"] = tXAnchor.Fee,
      ["heightConfirmed"] = 0
    });

    TXAnchorOpen = (TXBitcoin)tXAnchor;

    return true;
  }

  protected override void InsertBlock(Block block)
  {
    int height = block.Header.Height;

    int heightPruning = height - MAX_COUNT_BLOCKS_STORED;

    OutputsOwned.DeleteMany(Query.Between("HeightSpent", 1, heightPruning));
    AnchorsOwned.DeleteMany(Query.Between("heightConfirmed", 1, heightPruning));

    foreach (TXBitcoin tX in block.TXs)
    {
      foreach (TXInputBitcoin tXInput in tX.Inputs)
      {
        if (OutputsOwned.FindById(TXOutputOwned.CreateID(tXInput.TXIDOutput, tXInput.OutputIndex)) is TXOutputOwned outputSpent)
        {
          outputSpent.HeightSpent = height;
          OutputsOwned.Update(outputSpent);
        }

        if (TXAnchorOpen != null &&
          TXAnchorOpen.Inputs.Any(i => i.TXIDOutput.IsAllBytesEqual(tXInput.TXIDOutput) && i.OutputIndex == tXInput.OutputIndex))
        {
          BsonDocument documentAnchor = AnchorsOwned.FindById(TXAnchorOpen.Hash);
          documentAnchor["heightConfirmed"] = height;
          AnchorsOwned.Update(documentAnchor);

          TXAnchorOpen = null;
        }
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

    if (AnchorsOwned.FindOne(Query.EQ("heightConfirmed", height)) is BsonDocument documentAnchor)
    {
      AnchorsOwned.DeleteMany(Query.EQ("heightConfirmed", 0));

      documentAnchor["heightConfirmed"] = 0;
      AnchorsOwned.Update(documentAnchor);

      LoadTXAnchorOpen(documentAnchor);
    }
  }
}
