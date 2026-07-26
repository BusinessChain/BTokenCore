using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public partial class TokenBitcoin : Token
  {
    public class TXBitcoin : TX
    {
      public List<TXInputBitcoin> Inputs = new();


      public TXBitcoin()
      { }

      public TXBitcoin(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
      {
        int indexTxStart = index;

        index += 4; // Version

        int countInputs = VarInt.GetInt(buffer, ref index);

        if (countInputs == 0x00)
          throw new NotSupportedException("Segwit is not implemented.");

        for (int i = 0; i < countInputs; i++)
          Inputs.Add(new TXInputBitcoin(buffer, ref index));

        int countTXOutputs = VarInt.GetInt(buffer, ref index);

        for (int i = 0; i < countTXOutputs; i++)
        {
          TXOutputs.Add(ParseTXOutputBitcoin(buffer, ref index));
        }

        index += 4; //BYTE_LENGTH_LOCK_TIME

        Hash = sHA256.ComputeHash(sHA256.ComputeHash(buffer, indexTxStart, index - indexTxStart));
      }

      TXOutput ParseTXOutputBitcoin(byte[] buffer, ref int startIndex)
      {
        double value = BitConverter.ToInt64(buffer, startIndex);
        startIndex += 8;

        int lengthScript = VarInt.GetInt(buffer, ref startIndex);

        if (lengthScript == LENGTH_SCRIPT_P2PKH &&
          PREFIX_P2PKH.IsAllBytesEqual(buffer, startIndex))
        {
          return new TXOutputBitcoin(buffer, ref startIndex);
        }
        else if (lengthScript == TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN &&
          TXOutputTokenAnchor.PREFIX_ANCHOR_TOKEN.IsAllBytesEqual(buffer, startIndex))
        {
          return new TXOutputTokenAnchor(buffer, ref startIndex);
        }
        else
          return null;
      }


      public override void Serialize(Wallet wallet)
      {
        List<byte> tXRaw = new();

        tXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // version

        tXRaw.Add((byte)Inputs.Count);
        foreach (TXInputBitcoin input in Inputs)
        {
          tXRaw.AddRange(input.TXIDOutput);
          tXRaw.AddRange(BitConverter.GetBytes(input.OutputIndex));
          tXRaw.Add(0x00); // length empty script
          tXRaw.AddRange(BitConverter.GetBytes(input.Sequence));
        }

        tXRaw.Add((byte)TXOutputs.Count);
        foreach(TXOutputBitcoin output in TXOutputs)
        {
          tXRaw.AddRange(BitConverter.GetBytes(output.Value));
          tXRaw.AddRange(VarInt.GetBytes(output.Script.Length));
          tXRaw.AddRange(output.Script);
        }

        tXRaw.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // locktime
        tXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // sighash

        SignTX(tXRaw, wallet);

        TXRaw = tXRaw.ToArray();
      }

      void SignTX(List<byte> tXRaw, Wallet wallet)
      {
        List<List<byte>> signaturesPerInput = new();
        int indexFirstInput = 5;

        for (int i = 0; i < Inputs.Count; i++)
        {
          List<byte> tXRawSign = tXRaw.ToList();
          int indexRawSign = indexFirstInput + 36 * (i + 1) + 5 * i;

          byte[] publicScript = PREFIX_P2PKH.Concat(wallet.Hash160PKeyPublic).Concat(POSTFIX_P2PKH).ToArray();

          tXRawSign[indexRawSign++] = (byte)publicScript.Length;
          tXRawSign.InsertRange(indexRawSign, publicScript);

          byte[] message = wallet.SHA256.ComputeHash(tXRawSign.ToArray());

          byte[] signature = wallet.GetSignature(message);

          List<byte> scriptSig = new();

          scriptSig.Add((byte)(signature.Length + 1));
          scriptSig.AddRange(signature);
          scriptSig.Add(0x01);

          scriptSig.Add((byte)wallet.KeyPublic.Length);
          scriptSig.AddRange(wallet.KeyPublic);

          signaturesPerInput.Add(scriptSig);
        }

        for (int i = Inputs.Count - 1; i >= 0; i -= 1)
        {
          int indexSig = indexFirstInput + 36 * (i + 1) + 5 * i;

          tXRaw[indexSig++] = (byte)signaturesPerInput[i].Count;

          tXRaw.InsertRange(indexSig, signaturesPerInput[i]);
        }

        tXRaw.RemoveRange(tXRaw.Count - 4, 4);
      }
    }
  }
}