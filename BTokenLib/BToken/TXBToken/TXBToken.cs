using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public partial class TokenBToken : Token
  {
    public class TXBToken : TX
    {
      const int LENGTH_PUBKEYCOMPRESSED = 33;

      /// <summary>
      /// The PublicKey specifies from which account the funds are sourced.
      /// </summary>
      public byte[] KeyPublic = new byte[LENGTH_PUBKEYCOMPRESSED];

      public const int LENGTH_IDACCOUNT = 20;

      /// <summary>
      /// IDAccountSource is derived from the PublicKey and is used to address the account in the database.
      /// </summary>
      public byte[] IDAccountSource = new byte[LENGTH_IDACCOUNT];

      /// <summary>
      /// In order for the transaction to be valid, the nonce must be equal to the nonce of the 
      /// account source.
      /// </summary>
      public int Nonce;

      /// <summary>
      /// This has to match the block height at which the account source was created.
      /// It is in a sense an extension of the nonce.
      /// </summary>
      public int BlockheightAccountCreated;


      public TXBToken()
      { }

      public TXBToken(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
      {
        int indexTxStart = index;

        Array.Copy(buffer, index, KeyPublic, 0, KeyPublic.Length);
        index += KeyPublic.Length;

        IDAccountSource = Crypto.ComputeHash160(KeyPublic, sHA256);

        BlockheightAccountCreated = BitConverter.ToInt32(buffer, index);
        index += 4;

        Nonce = BitConverter.ToInt32(buffer, index);
        index += 4;

        Fee = BitConverter.ToInt64(buffer, index);
        index += 8;

        int countOutputs = VarInt.GetInt(buffer, ref index);

        for (int i = 0; i < countOutputs; i++)
          TXOutputs.Add(ParseTXOutputBToken(buffer, ref index));

        Hash = sHA256.ComputeHash(sHA256.ComputeHash(buffer, indexTxStart, index - indexTxStart));

        if (!flagIsCoinbase)
          VerifySignatureTX(indexTxStart, buffer, ref index);

        CountBytes = index - indexTxStart;
      }

      TXOutput ParseTXOutputBToken(byte[] buffer, ref int startIndex)
      {
        double value = BitConverter.ToInt64(buffer, startIndex);
        startIndex += 8;

        int lengthScript = VarInt.GetInt(buffer, ref startIndex);

        if (lengthScript == LENGTH_SCRIPT_P2PKH &&
          PREFIX_P2PKH.IsAllBytesEqual(buffer, startIndex))
        {
          return new TXOutputP2PKH(buffer, ref startIndex);
        }
        else if (lengthScript == TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN &&
          TXOutputTokenAnchor.PREFIX_ANCHOR_TOKEN.IsAllBytesEqual(buffer, startIndex))
        {
          return new TXOutputTokenAnchor(buffer, ref startIndex);
        }
        else
          return null;
      }

      public void VerifySignatureTX(int indexTxStart, byte[] buffer, ref int index)
      {
        int lengthMessage = index - indexTxStart;

        int lengthSig = buffer[index++];
        byte[] signature = new byte[lengthSig];
        Array.Copy(buffer, index, signature, 0, lengthSig);
        index += lengthSig;

        if (!Crypto.VerifySignature(buffer, indexTxStart, lengthMessage, KeyPublic, signature))
          throw new ProtocolException($"TX {this} contains invalid signature.");
      }
      
      public override void Serialize(Wallet wallet)
      {
        List<byte> tXRaw = new();

        tXRaw.AddRange(KeyPublic);
        tXRaw.AddRange(BitConverter.GetBytes(BlockheightAccountCreated));
        tXRaw.AddRange(BitConverter.GetBytes(Nonce));
        tXRaw.AddRange(BitConverter.GetBytes(Fee));

        tXRaw.AddRange(VarInt.GetBytes(TXOutputs.Count));
        foreach (TXOutputP2PKH output in TXOutputs)
        {
          tXRaw.Add((byte)output.Type);
          tXRaw.AddRange(output.Script);
        }

        byte[] signature = wallet.GetSignature(tXRaw.ToArray());

        tXRaw.Add((byte)signature.Length);
        tXRaw.AddRange(signature);

        TXRaw = tXRaw.ToArray();
      }
    }
  }
}

