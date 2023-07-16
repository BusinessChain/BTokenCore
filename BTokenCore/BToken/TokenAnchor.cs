using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  partial class TokenBToken
  {
    public class TokenAnchor
    {
      public byte[] IDToken;

      public List<TXOutputWallet> Inputs = new();

      public int NumberSequence;

      public byte[] HashBlockReferenced = new byte[32];
      public byte[] HashBlockPreviousReferenced = new byte[32];

      public long ValueChange;

      byte OP_RETURN = 0x6A;

      public TX TX;



      public TokenAnchor()
      {
        TX = new();
      }

      public TokenAnchor(TX tX, int index, byte[] idToken)
      {
        TX = tX;

        IDToken = idToken;

        Array.Copy(
          TX.TXOutputs[0].Buffer,
          index,
          HashBlockReferenced,
          0,
          HashBlockReferenced.Length);

        index += 32;

        Array.Copy(
          TX.TXOutputs[0].Buffer,
          index,
          HashBlockPreviousReferenced,
          0,
          HashBlockPreviousReferenced.Length);

        ValueChange = tX.TXOutputs[1].Value;
      }

      public void GetInputPublicKey()
      {
        // Damit ich weiss an wen die BlockReward gehen soll
        // Es ist die Adresse des Senders des AnkerTokens
        byte[] scriptSig = TX.TXInputs[0].ScriptPubKey;

        int startIndex = 0;
        int lengthScript = scriptSig[startIndex++];
        startIndex += lengthScript;

        int lengthPubkey = scriptSig[startIndex++];
        var publicKey = new byte[lengthPubkey];
        Array.Copy(scriptSig, startIndex, publicKey, 0, lengthPubkey);

        //var hashPublicKey = Wallet.ComputeHash160Pubkey(publicKey);
      }


      public void Serialize(Token tokenParent, SHA256 sHA256)
      {
        List<byte> tXRaw = new();
        long feeTX = 0;

        tXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // version
        tXRaw.Add((byte)Inputs.Count);

        int indexFirstInput = tXRaw.Count;

        for (int i = 0; i < Inputs.Count; i += 1)
        {
          tXRaw.AddRange(Inputs[i].TXID);
          tXRaw.AddRange(BitConverter.GetBytes(Inputs[i].Index));
          tXRaw.Add(0x00); // length empty script
          tXRaw.AddRange(BitConverter.GetBytes(NumberSequence)); // sequence

          feeTX += Inputs[i].Value;
        }

        byte[] dataAnchorToken = IDToken.Concat(HashBlockReferenced)
          .Concat(HashBlockPreviousReferenced).ToArray();

        tXRaw.Add((byte)(ValueChange > 0 ? 2 : 1));
        tXRaw.AddRange(BitConverter.GetBytes((ulong)0));
        tXRaw.Add(LENGTH_DATA_ANCHOR_TOKEN + 2);
        tXRaw.Add(OP_RETURN);
        tXRaw.Add(LENGTH_DATA_ANCHOR_TOKEN);
        tXRaw.AddRange(dataAnchorToken);

        Wallet wallet = tokenParent.Wallet;

        if (ValueChange > 0)
        {
          tXRaw.AddRange(BitConverter.GetBytes(ValueChange));
          tXRaw.Add((byte)wallet.PublicScript.Length);
          tXRaw.AddRange(wallet.PublicScript);

          feeTX -= ValueChange;
        }

        tXRaw.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // locktime
        tXRaw.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 }); // sighash

        List<List<byte>> signaturesPerInput = new();

        for (int i = 0; i < Inputs.Count; i += 1)
        {
          List<byte> tXRawSign = tXRaw.ToList();
          int indexRawSign = indexFirstInput + 36 * (i + 1) + 5 * i;

          tXRawSign[indexRawSign++] = (byte)wallet.PublicScript.Length;
          tXRawSign.InsertRange(indexRawSign, wallet.PublicScript);

          signaturesPerInput.Add(
            wallet.GetScriptSignature(tXRawSign.ToArray()));
        }

        for (int i = Inputs.Count - 1; i >= 0; i -= 1)
        {
          int indexSign = indexFirstInput + 36 * (i + 1) + 5 * i;

          tXRaw[indexSign++] = (byte)signaturesPerInput[i].Count;

          tXRaw.InsertRange(
            indexSign,
            signaturesPerInput[i]);
        }

        tXRaw.RemoveRange(tXRaw.Count - 4, 4);

        int index = 0;

        TX = Block.ParseTX(
          isCoinbase: false,
          tXRaw.ToArray(),
          ref index,
          sHA256);

        TX.TXRaw = tXRaw;

        TX.Hash = sHA256.ComputeHash(
         sHA256.ComputeHash(tXRaw.ToArray()));

        TX.Fee = feeTX;
      }

      public override string ToString()
      {
        return TX.ToString();
      }

      public string GetDescription()
      {
        return
          $"Anchor token TX hash: {TX}\n" +
          $"Number of inputs: {TX.TXInputs.Count}\n" +
          $"Fee: {TX.Fee}\n" +
          $"ValueChange: {ValueChange}\n" +
          $"Sequence number: {NumberSequence}\n" +
          $"Referenced BToken block hash: {HashBlockReferenced.ToHexString()}" +
          $"Referenced BToken previous block hash: {HashBlockPreviousReferenced.ToHexString()}";
      }
    }
  }
}
