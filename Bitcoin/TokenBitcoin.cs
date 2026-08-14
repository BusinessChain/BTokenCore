using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenCore;

public partial class TokenBitcoin : Token
{
  class EqualityComparerTXOutputWallet : IEqualityComparer<TXOutputWallet>
  {
    public bool Equals(TXOutputWallet x, TXOutputWallet y)
    {
      return x.Index == y.Index && x.TXID.IsAllBytesEqual(y.TXID);
    }

    public int GetHashCode(TXOutputWallet x)
    {
      return BitConverter.ToInt32(x.TXID, 0) + x.Index;
    }
  }

  const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

  const int LENGTH_P2PKH_INPUT = 148;
  const int LENGTH_P2PKH_OUTPUT = 34;
  const int LENGTH_TX_OVERHEAD = 10;

  List<TXOutputWallet> OutputsSpendable = new();
  List<TXOutputWallet> OutputsSpendableConfirmed = new();


  public TokenBitcoin(IEnvironment environment)
    : base(environment)
  {
    SizeBlockMax = SIZE_BLOCK_MAX;

    IDToken = [(byte)'B', (byte)'T', (byte)'C'];
    Port = 8333;

    Network = new Network(
      null,
      this,
      flagEnableInboundConnections: false,
      flagEnableRelay: false);
  }

  internal override Header CreateHeaderGenesis()
  {
    //HeaderBitcoin header = new(
    //   headerHash: "0000000000000000000230d9bb1db81e56916b0c2c7363231e75b82b24714482".ToBinary(),
    //   version: 0x01,
    //   hashPrevious: "00000000000000000008b5ffa0ae1b604dd27bf4af84602ea53f7920320a3c96".ToBinary(),
    //   merkleRootHash: "ef303d1cf8090e1bcea36432eceea2bbc156e81108deff1616d9c6dee64ba7c7".ToBinary(),
    //   unixTimeSeconds: 1653490985, // take timestamp from trezor.io explorer and convert to epoch time GMT
    //   nBits: 386492960,
    //   nonce: 578608666);

    //header.Height = 737856; // Should be modulo 2016 so it calculates next target bits correctly.

    HeaderBitcoin header = new HeaderBitcoin(
       headerHash: "000000A13F15EC9FECECAB8EF438F8E16E729AC2AF816C3DBE7E27BAF110F66A".ToBinary(),
       version: 0x01,
       hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
       merkleRootHash: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
       unixTimeSeconds: 1667333891,
       //nBits: 0x1d4fffff,
       nBits: 0x1dffffff,
       nonce: 1441757173);

    header.Height = 0; // Should be modulo 2016 so it calculates next target bits correctly.

    header.DifficultyAccumulated = header.Difficulty;

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

  internal override bool TryGetTX(byte[] hash, out TX tX)
  {
    tX = null;
    return false;
  }

  internal override bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte, out TX tXAnchor)
  {
    tXAnchor = new TXBitcoin();

    //return new()
    //{
    //  new TXOutputWallet()
    //  {
    //    TXID = "20da7491ec53757a914dc1f045afbcb0a5c3396785a9abe9fc074e017e9403fd".ToBinary(),
    //    Value = 7106,
    //    Index = 1
    //  }
    //};

    long feePerInputP2PKH = (LENGTH_P2PKH_INPUT * feePerByte);
    long feePerOutputP2PKH = (LENGTH_P2PKH_OUTPUT * feePerByte);

    List<TXOutputWallet> outputsSpendable = OutputsSpendableConfirmed
      .Where(o => o.Value > feePerInputP2PKH)
      .Take(VarInt.PREFIX_UINT16 - 1).ToList();

    long valueInputs = outputsSpendable.Sum(o => o.Value);

    byte[] tokenAnchorRaw = tokenAnchor.Serialize();

    long feeTX = (long)(feePerByte
      * (LENGTH_P2PKH_INPUT * outputsSpendable.Count
      + LENGTH_TX_OVERHEAD
      + tokenAnchorRaw.Length));

    if (valueInputs < feeTX + tokenAnchor.Value)
      return false;

    long valueChange = valueInputs - tokenAnchor.Value - feeTX - feePerOutputP2PKH;

    bool flagCreateOutputChange = valueChange > feePerInputP2PKH;

    foreach (TXOutputWallet outputSpendable in outputsSpendable)
    {
      ((TXBitcoin)tXAnchor).Inputs.Add(new TXInputBitcoin
      {
        TXIDOutput = outputSpendable.TXID,
        OutputIndex = outputSpendable.Index
      });
    }

    tXAnchor.TXOutputs.Add(tokenAnchor);

    if (flagCreateOutputChange)
      tXAnchor.TXOutputs.Add(new TXOutputBitcoin
      {
        Type = TXOutput.TypesToken.P2PKH,
        Value = valueChange,
        Script = PREFIX_P2PKH.Concat(Wallet.Hash160PKeyPublic).Concat(POSTFIX_P2PKH).ToArray()
      });

    tXAnchor.Serialize(Wallet);

    return true;
  }

  internal override string[] GetSeedAddresses()
  {
    return
      [
        "seed.bitcoin.sipa.be",
        "dnsseed.bluematt.me",
        "dnsseed.bitcoin.dashjr.org",
        "seed.bitcoinstats.com",
        "seed.bitnodes.io"
      ];
  }

  // Hier darf es keine Exception geben, weil wir einen
  // geparsten Bitcoin Block mit PoW immer als korrekt betrachten

  internal override void InsertBlock(Block block)
  {
    foreach (TXBitcoin tX in block.TXs)
    {
      foreach (TXInputBitcoin tXInput in tX.Inputs)
        OutputsSpendable.RemoveAll(o => o.TXID.IsAllBytesEqual(tXInput.TXIDOutput) && o.Index == tXInput.OutputIndex);

      for (int i = 0; i < tX.TXOutputs.Count; i++)
        if (TryAddTXOutputWallet(OutputsSpendable, tX, i))
          IndexTXs.Add(tX.Hash, tX);
    }
  }

  // Das muss eine Datanbank sein!!
  internal Dictionary<byte[], TX> IndexTXs = new(new EqualityComparerByteArray());

  internal override void ReverseBlock(Block block)
  {
    for (int t = block.TXs.Count - 1; t >= 0; t--)
    {
      TXBitcoin tX = block.TXs[t] as TXBitcoin;

      OutputsSpendable.RemoveAll(o => o.TXID.IsAllBytesEqual(tX.Hash));

      foreach (TXInputBitcoin tXInput in tX.Inputs)
      {
        TX tXReferenced = block.TXs.Find(t => t.Hash.IsAllBytesEqual(tXInput.TXIDOutput));

        if (tXReferenced != null || IndexTXs.TryGetValue(tXInput.TXIDOutput, out tXReferenced))
          TryAddTXOutputWallet(OutputsSpendable, tXReferenced as TXBitcoin, tXInput.OutputIndex);
      }

      IndexTXs.Remove(tX.Hash);
    }
  }

  bool TryAddTXOutputWallet(List<TXOutputWallet> listOutputs, TXBitcoin tX, int indexOutput)
  {
    TXOutputBitcoin tXOutputReferenced = (TXOutputBitcoin)tX.TXOutputs[indexOutput];

    if (tXOutputReferenced.Type == TXOutput.TypesToken.P2PKH &&
      tXOutputReferenced.PublicKeyHash160.IsAllBytesEqual(Wallet.Hash160PKeyPublic))
    {
      listOutputs.Add(
        new TXOutputWallet
        {
          TXID = tX.Hash,
          Index = indexOutput,
          Value = tXOutputReferenced.Value
        });

      return true;
    }

    return false;
  }
}
