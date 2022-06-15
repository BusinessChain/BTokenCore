using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  class TokenAnchor : TX
  {
    public byte[] HashBlock = new byte[32];
    public byte[] HashPrevious = new byte[32];

    public List<TXOutputWallet> Inputs = new();
    public byte[] DataAnchorToken;

    public long ValueChange;

    byte OP_RETURN = 0x6A;


    public TokenAnchor(byte[] buffer, int index)
    {
      Array.Copy(buffer, index, HashBlock, 0, HashBlock.Length);
    }

    public TokenAnchor()
    { }


    public void Serialize(Wallet wallet, SHA256 sHA256)
    {
      TXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // version
      TXRaw.Add((byte)Inputs.Count);

      int indexFirstInput = TXRaw.Count;

      for (int i = 0; i < Inputs.Count; i += 1)
      {
        TXRaw.AddRange(Inputs[i].TXID);
        TXRaw.AddRange(BitConverter.GetBytes(Inputs[i].OutputIndex));
        TXRaw.Add(0x00); // length empty script
        TXRaw.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }); // sequence
      }

      TXRaw.Add((byte)(ValueChange > 0 ? 2 : 1));
      TXRaw.AddRange(BitConverter.GetBytes((ulong)0));
      TXRaw.Add((byte)(DataAnchorToken.Length + 2));
      TXRaw.Add(OP_RETURN);
      TXRaw.Add((byte)DataAnchorToken.Length);
      TXRaw.AddRange(DataAnchorToken);

      if (ValueChange > 0)
      {
        TXRaw.AddRange(BitConverter.GetBytes(ValueChange));
        TXRaw.Add((byte)wallet.PublicInputScript.Length);
        TXRaw.AddRange(wallet.PublicInputScript);
      }

      TXRaw.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // locktime
      TXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // sighash

      List<List<byte>> signaturesPerInput = new();

      for (int i = 0; i < Inputs.Count; i += 1)
      {
        List<byte> tXRawSign = TXRaw.ToList();
        int indexRawSign = indexFirstInput + 36 * (i + 1) + 5 * i;

        tXRawSign[indexRawSign++] = (byte)wallet.PublicInputScript.Length;
        tXRawSign.InsertRange(indexRawSign, wallet.PublicInputScript);

        signaturesPerInput.Add(
          wallet.GetScriptSignature(tXRawSign.ToArray()));
      }

      for (int i = Inputs.Count - 1; i >= 0; i -= 1)
      {
        int indexSign = indexFirstInput + 36 * (i + 1) + 5 * i;

        TXRaw[indexSign++] = (byte)signaturesPerInput[i].Count;

        TXRaw.InsertRange(
          indexSign,
          signaturesPerInput[i]);
      }

      TXRaw.RemoveRange(TXRaw.Count - 4, 4);

      Hash = sHA256.ComputeHash(
       sHA256.ComputeHash(TXRaw.ToArray()));
    }      
  }
}
