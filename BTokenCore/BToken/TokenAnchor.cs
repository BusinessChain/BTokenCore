using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using BTokenLib;

namespace BTokenCore
{
  class TokenAnchor : TX
  {
    public byte[] HashBlock = new byte[32];
    public byte[] HashPrevious = new byte[32];

    public List<TXOutputWallet> Inputs = new();
    public List<byte[]> DataAnchorTokens = new();

    public ulong ValueChange;


    public TokenAnchor(byte[] buffer, int index)
    {
      Array.Copy(buffer, index, HashBlock, 0, HashBlock.Length);
    }

    public TokenAnchor()
    { }


    const int LENGTH_P2PKH = 25;
    byte OP_RETURN = 0x6A;
    byte[] PREFIX_P2PKH = new byte[] { 0x76, 0xA9, 0x14 };

    byte[] POSTFIX_P2PKH = new byte[] { 0x88, 0xAC };
    byte[] PublicKeyHash160 = new byte[20];

    public void Serialize(Wallet wallet)
    {
      List<byte> tXRaw = new();

      tXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // version
      tXRaw.Add((byte)Inputs.Count);

      int indexFirstInput = tXRaw.Count;

      for (int i = 0; i < Inputs.Count; i += 1)
      {
        tXRaw.AddRange(Inputs[i].TXID);
        tXRaw.AddRange(BitConverter.GetBytes(Inputs[i].OutputIndex));
        tXRaw.Add(0x00); // length empty script
        tXRaw.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }); // sequence
      }

      tXRaw.Add(CountOutputs);

      for (int i = 0; i < CountAnchorToken; i += 0)
      {
        tXRaw.AddRange(BitConverter.GetBytes((ulong)0));
        tXRaw.Add((byte)(DataAnchorTokens.Count + 2));
        tXRaw.Add(OP_RETURN);
        tXRaw.Add((byte)DataAnchorTokens.Count);
        tXRaw.AddRange(DataAnchorTokens[i]);
      }

      if (ValueChange > 0)
      {
        tXRaw.AddRange(BitConverter.GetBytes(ValueChange));
        tXRaw.Add(LENGTH_P2PKH);
        tXRaw.AddRange(PREFIX_P2PKH);
        tXRaw.AddRange(PublicKeyHash160);
        tXRaw.AddRange(POSTFIX_P2PKH);
      }

      tXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // locktime
      tXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // sighash


      List<List<byte>> signaturesPerInput = new();

      for (int i = 0; i < Inputs.Count; i += 1)
      {
        List<byte> tXRawSign = tXRaw.ToList();
        int indexRawSign = indexFirstInput + 36 * (i + 1) + 5 * i;

        tXRawSign[indexRawSign++] = LENGTH_P2PKH;
        tXRawSign.InsertRange(indexRawSign, Inputs[i].ScriptPubKey);

        signaturesPerInput.Add(wallet.GetScriptSignature(
          tXRawSign.ToArray()));
      }

      for (int i = 0; i < Inputs.Count; i += 1)
      {
        // one by one insert the signature and public key in the placeholder
      }


      tXRaw.RemoveRange(tXRaw.Count - 4, 4);

      BlockBitcoin parser = new();
      int indexTXRaw = 0;
      byte[] tXRawArray = tXRaw.ToArray();

      TX tX = parser.ParseTX(
        false,
        tXRawArray,
        ref indexTXRaw);

      tX.TXRaw = tXRawArray;
    }      
  }
}
