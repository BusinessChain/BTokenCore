using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;

using BTokenLib;

namespace BTokenCore
{
  partial class UTXOTable
  {
    public partial class WalletUTXO
    {
      Crypto Crypto = new();

      string PrivKeyDec;
      
      List<TXOutputWallet> TXOutputsSpendable = new();


      readonly byte[] PREFIX_OP_RETURN =
        new byte[] { 0x6A, 0x50 };

      byte[] PREFIX_P2PKH =
        new byte[] { 0x76, 0xA9, 0x14 };

      byte[] POSTFIX_P2PKH = new byte[] { 0x88, 0xAC };

      byte OP_RETURN = 0x6A;

      const int LENGTH_P2PKH = 25;

      SHA256 SHA256 = SHA256.Create();
      readonly RipeMD160Digest RIPEMD160 = new();
      byte[] PublicKeyHash160 = new byte[20];



      public WalletUTXO()
      {
        PrivKeyDec = File.ReadAllText("Wallet/wallet");

        byte[] publicKey = Crypto.GetPubKeyFromPrivKey(PrivKeyDec);

        GeneratePublicKeyHash160(publicKey);
      }

      void GeneratePublicKeyHash160(byte[] publicKey)
      {
        var p = SHA256.ComputeHash(publicKey);

        RIPEMD160.BlockUpdate(p, 0, p.Length);
        RIPEMD160.DoFinal(PublicKeyHash160, 0);
      }

      public string GetStatus()
      {
        string outputsSpendable = 
          TXOutputsSpendable.Any() ? "" : "Wallet empty.";

        foreach(var output in TXOutputsSpendable)
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

          tXOutput.Value = BitConverter.ToUInt64(buffer, index);
          index += 8;
          
          tXOutput.ScriptPubKey = new byte[LENGTH_P2PKH];
          Array.Copy(buffer, index, tXOutput.ScriptPubKey, 0, LENGTH_P2PKH);
          index += LENGTH_P2PKH;

          TXOutputsSpendable.Add(tXOutput);
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
          foreach(TXOutputWallet tXOutput in TXOutputsSpendable)
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

      public void DetectTXOutputsSpendable(TX tX)
      {
        for (int i = 0; i < tX.TXOutputs.Count; i += 1)
        {
          TXOutput tXOutput = tX.TXOutputs[i];

          if (tXOutput.LengthScript != LENGTH_P2PKH)
          {
            continue;
          }

          int indexScript = tXOutput.StartIndexScript;

          if (!PREFIX_P2PKH.IsEqual(
            tXOutput.Buffer,
            indexScript))
          {
            continue;
          }

          indexScript += 3;

          if (!PublicKeyHash160.IsEqual(
            tXOutput.Buffer,
            indexScript))
          {
            continue;
          }

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

            TXOutputsSpendable.Add(
              new TXOutputWallet
              {
                TXID = tX.Hash,
                TXIDShort = tX.TXIDShort,
                OutputIndex = i,
                Value = tXOutput.Value,
                ScriptPubKey = scriptPubKey
              });

            Console.WriteLine(
              "Detected spendable output {0} " +
              "in tx {1} with {2} satoshis.",
              i,
              tX.Hash.ToHexString(),
              tXOutput.Value);
          }
        }
      }

      public bool TrySpend(TXInput tXInput)
      {
        TXOutputWallet output = 
          TXOutputsSpendable.Find(o => 
          o.TXIDShort == tXInput.TXIDOutputShort &&
          o.OutputIndex == tXInput.OutputIndex);

        if (output == null ||
          !output.TXID.IsEqual(tXInput.TXIDOutput))
        {
          return false;
        }

        TXOutputsSpendable.Remove(output);

        Console.WriteLine(
          "Spent output {0} in tx {1} with {2} satoshis.",
          output.OutputIndex,
          output.TXID.ToHexString(),
          output.Value);

        return true;
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

      //public TX CreateAnchorToken(
      //  byte[] dataOPReturn)
      //{
      //  ulong fee = 28000;

      //  TXOutputWallet outputSpendable =
      //    TXOutputsSpendable.Find(t => t.Value > fee);

      //  if (outputSpendable == null)
      //    throw new ProtocolException("No spendable output found.");

      //  List<byte> tXRaw = new();

      //  byte[] version = { 0x01, 0x00, 0x00, 0x00 };
      //  tXRaw.AddRange(version);

      //  byte countInputs = 1;
      //  tXRaw.Add(countInputs);

      //  tXRaw.AddRange(outputSpendable.TXID);

      //  tXRaw.AddRange(BitConverter.GetBytes(
      //    outputSpendable.OutputIndex));

      //  int indexScriptSig = tXRaw.Count;

      //  tXRaw.Add(LENGTH_P2PKH);

      //  tXRaw.AddRange(outputSpendable.ScriptPubKey);

      //  byte[] sequence = { 0xFF, 0xFF, 0xFF, 0xFF };
      //  tXRaw.AddRange(sequence);

      //  byte countOutputs = 2; //(byte)(valueChange == 0 ? 1 : 2);
      //  tXRaw.Add(countOutputs);

      //  ulong valueChange = outputSpendable.Value - fee;
      //  tXRaw.AddRange(BitConverter.GetBytes(
      //    valueChange));

      //  tXRaw.Add(LENGTH_P2PKH);

      //  tXRaw.AddRange(PREFIX_P2PKH);
      //  tXRaw.AddRange(PublicKeyHash160);
      //  tXRaw.AddRange(POSTFIX_P2PKH);

      //  tXRaw.AddRange(BitConverter.GetBytes(
      //    (ulong)0));

      //  tXRaw.Add((byte)(dataOPReturn.Length + 2));
      //  tXRaw.Add(OP_RETURN);
      //  tXRaw.Add((byte)dataOPReturn.Length);
      //  tXRaw.AddRange(dataOPReturn);

      //  var lockTime = new byte[4];
      //  tXRaw.AddRange(lockTime);

      //  byte[] sigHashType = { 0x01, 0x00, 0x00, 0x00 };
      //  tXRaw.AddRange(sigHashType);

      //  byte[] signature = Crypto.GetSignature(
      //  PrivKeyDec,
      //  tXRaw.ToArray());

      //  var scriptSig = new List<byte>();
      //  scriptSig.Add((byte)(signature.Length + 1));
      //  scriptSig.AddRange(signature);
      //  scriptSig.Add(0x01);

      //  byte[] publicKey = Crypto.GetPubKeyFromPrivKey(PrivKeyDec);

      //  scriptSig.Add((byte)publicKey.Length);
      //  scriptSig.AddRange(publicKey);

      //  var tXRawPreScriptSig = tXRaw.Take(indexScriptSig);
      //  var tXRawPostScriptSig = tXRaw.Skip(indexScriptSig + LENGTH_P2PKH + 1);

      //  tXRaw = tXRawPreScriptSig
      //    .Concat(new byte[] { (byte)scriptSig.Count })
      //    .Concat(scriptSig)
      //    .Concat(tXRawPostScriptSig)
      //    .ToList();

      //  tXRaw.RemoveRange(tXRaw.Count - 4, 4);

      //  var parser = new ParserBitcoin();
      //  int indexTXRaw = 0;
      //  byte[] tXRawArray = tXRaw.ToArray();

      //  TX tX = parser.ParseTX(
      //    false,
      //    tXRawArray,
      //    ref indexTXRaw);

      //  tX.TXRaw = tXRawArray;

      //  return tX;
      //}
    }
  }
}
