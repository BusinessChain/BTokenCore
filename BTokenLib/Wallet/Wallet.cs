﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;


namespace BTokenLib
{
  public partial class Wallet
  {
    const int HASH_BYTE_SIZE = 32;

    Crypto Crypto = new();

    string PrivKeyDec;

    List<TXOutputWallet> TXOutputsSortedValueDescending = new();

    const int LENGTH_P2PKH = 25;
    byte[] PREFIX_P2PKH = new byte[] { 0x76, 0xA9, 0x14 };
    byte[] POSTFIX_P2PKH = new byte[] { 0x88, 0xAC };

    SHA256 SHA256 = SHA256.Create();
    readonly RipeMD160Digest RIPEMD160 = new();
    public byte[] PublicKeyHash160 = new byte[20];



    public Wallet()
    {
      PrivKeyDec = File.ReadAllText("Wallet/wallet");

      var hashPublicKey = SHA256.ComputeHash(
        Crypto.GetPubKeyFromPrivKey(PrivKeyDec));

      RIPEMD160.BlockUpdate(hashPublicKey, 0, hashPublicKey.Length);
      RIPEMD160.DoFinal(PublicKeyHash160, 0);
    }


    public List<byte> GetScriptSignature(byte[] tXRaw)
    {
      byte[] signature = Crypto.GetSignature(
      PrivKeyDec,
      tXRaw.ToArray());

      byte[] publicKey = Crypto.GetPubKeyFromPrivKey(PrivKeyDec);

      List<byte> scriptSig = new();
      scriptSig.Add((byte)(signature.Length + 1));
      scriptSig.AddRange(signature);
      scriptSig.Add(0x01);

      scriptSig.Add((byte)publicKey.Length);
      scriptSig.AddRange(publicKey);

      return scriptSig;
    }


    public string GetStatus()
    {
      string outputsSpendable =
        TXOutputsSortedValueDescending.Any() ? "" : "Wallet empty.";

      foreach (var output in TXOutputsSortedValueDescending)
      {
        outputsSpendable += $"TXID: {output.TXID.ToHexString()}\n";
        outputsSpendable += $"Output Index: {output.OutputIndex}\n";
        outputsSpendable += $"Value: {output.Value}\n";
      }

      return outputsSpendable;
    }

    public void LoadImage(string pathImage)
    {
      string pathFile = Path.Combine(
        pathImage, "ImageWallet");

      int index = 0;

      byte[] buffer = File.ReadAllBytes(pathFile);

      while (index < buffer.Length)
      {
        var tXOutput = new TXOutputWallet();

        tXOutput.TXID = new byte[HASH_BYTE_SIZE];
        Array.Copy(buffer, index, tXOutput.TXID, 0, HASH_BYTE_SIZE);
        index += HASH_BYTE_SIZE;

        tXOutput.TXIDShort = BitConverter.ToInt32(tXOutput.TXID, 0);

        tXOutput.OutputIndex = BitConverter.ToInt32(buffer, index);
        index += 4;

        tXOutput.Value = BitConverter.ToInt64(buffer, index);
        index += 8;

        tXOutput.ScriptPubKey = new byte[LENGTH_P2PKH];
        Array.Copy(buffer, index, tXOutput.ScriptPubKey, 0, LENGTH_P2PKH);
        index += LENGTH_P2PKH;

        TXOutputsSortedValueDescending.Add(tXOutput);
      }
    }

    public void CreateImage(string pathDirectory)
    {
      string pathimageWallet = Path.Combine(
         pathDirectory,
         "ImageWallet");

      using (var fileImageWallet =
        new FileStream(
          pathimageWallet,
          FileMode.Create,
          FileAccess.Write,
          FileShare.None))
      {
        foreach (TXOutputWallet tXOutput in TXOutputsSortedValueDescending)
        {
          fileImageWallet.Write(
            tXOutput.TXID, 0, tXOutput.TXID.Length);


          byte[] outputIndex = BitConverter.GetBytes(
            tXOutput.OutputIndex);

          fileImageWallet.Write(
            outputIndex, 0, outputIndex.Length);


          byte[] value = BitConverter.GetBytes(
            tXOutput.Value);

          fileImageWallet.Write(
            value, 0, value.Length);


          fileImageWallet.Write(
            tXOutput.ScriptPubKey, 0, tXOutput.ScriptPubKey.Length);
        }
      }
    }

    public void DetectTXOutputSpendable(TX tX, int indexOutput)
    {
      TXOutput tXOutput = tX.TXOutputs[indexOutput];

      if (tXOutput.LengthScript != LENGTH_P2PKH)
        return;

      int indexScript = tXOutput.StartIndexScript;

      if (!PREFIX_P2PKH.IsEqual(tXOutput.Buffer, indexScript))
        return;

      indexScript += 3;

      if (!PublicKeyHash160.IsEqual(tXOutput.Buffer, indexScript))
        return;

      indexScript += 20;

      if (POSTFIX_P2PKH.IsEqual(
        tXOutput.Buffer,
        indexScript))
      {
        byte[] scriptPubKey = new byte[LENGTH_P2PKH];

        Array.Copy(
          tXOutput.Buffer,
          tXOutput.StartIndexScript,
          scriptPubKey,
          0,
          LENGTH_P2PKH);

        TXOutputsSortedValueDescending.Add(
          new TXOutputWallet
          {
            TXID = tX.Hash,
            TXIDShort = tX.TXIDShort,
            OutputIndex = indexOutput,
            Value = tXOutput.Value,
            ScriptPubKey = scriptPubKey
          });

        //Console.WriteLine(
        //  "Detected spendable output {0} " +
        //  "in tx {1} with {2} satoshis.",
        //  i,
        //  tX.Hash.ToHexString(),
        //  tXOutput.Value);
      }
    }

    public bool TrySpend(TXInput tXInput)
    {
      TXOutputWallet output =
        TXOutputsSortedValueDescending.Find(o =>
        o.TXIDShort == tXInput.TXIDOutputShort &&
        o.OutputIndex == tXInput.OutputIndex);

      if (output == null ||
        !output.TXID.IsEqual(tXInput.TXIDOutput))
      {
        return false;
      }

      TXOutputsSortedValueDescending.Remove(output);

      Console.WriteLine(
        "Spent output {0} in tx {1} with {2} satoshis.",
        output.OutputIndex,
        output.TXID.ToHexString(),
        output.Value);

      return true;
    }

    public List<TXOutputWallet> GetOutputsSpendable()
    {
      return TXOutputsSortedValueDescending;
    }
    
    public byte[] GetReceptionScript()
    {
      byte[] script = new byte[26];

      script[0] = LENGTH_P2PKH;

      PREFIX_P2PKH.CopyTo(script, 1);
      PublicKeyHash160.CopyTo(script, 4);
      POSTFIX_P2PKH.CopyTo(script, 24);

      return script;
    }
  }
}