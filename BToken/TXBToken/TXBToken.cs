using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenCore;

public partial class TokenBToken : Token
{
  internal class TXBToken : TX
  {
    const int LENGTH_PUBKEYCOMPRESSED = 33;

    /// <summary>
    /// The PublicKey specifies from which account the funds are sourced.
    /// </summary>
    internal byte[] KeyPublic = new byte[LENGTH_PUBKEYCOMPRESSED];

    internal const int LENGTH_IDACCOUNT = 20;

    /// <summary>
    /// IDAccountSource is derived from the PublicKey and is used to address the account in the database.
    /// </summary>
    internal byte[] IDAccountSource = new byte[LENGTH_IDACCOUNT];

    /// <summary>
    /// In order for the transaction to be valid, the nonce must be equal to the nonce of the 
    /// account source.
    /// </summary>
    internal int Nonce;

    /// <summary>
    /// This has to match the block height at which the account source was created.
    /// It is in a sense an extension of the nonce.
    /// </summary>
    internal int BlockheightAccountCreated;


    internal TXBToken()
    { }

    internal TXBToken(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
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
      long value = BitConverter.ToInt64(buffer, startIndex);
      startIndex += 8;

      int lengthScript = VarInt.GetInt(buffer, ref startIndex);
      int indexScript = startIndex;
      startIndex += lengthScript;

      TXOutput tXOutput;

      if (lengthScript == LENGTH_SCRIPT_P2PKH &&
        PREFIX_P2PKH.IsAllBytesEqual(buffer, indexScript))
      {
        tXOutput = new TXOutputP2PKH(buffer, indexScript);
      }
      else if (lengthScript == TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN &&
        TXOutputTokenAnchor.PREFIX_ANCHOR_TOKEN.IsAllBytesEqual(buffer, indexScript))
      {
        tXOutput = new TXOutputTokenAnchor(buffer, ref indexScript);
      }
      else
        throw new ProtocolException("TX output script is neither P2PKH nor token anchor.");

      tXOutput.Value = value;

      return tXOutput;
    }

    internal void VerifySignatureTX(int indexTxStart, byte[] buffer, ref int index)
    {
      int lengthMessage = index - indexTxStart;

      int lengthSig = buffer[index++];
      byte[] signature = new byte[lengthSig];
      Array.Copy(buffer, index, signature, 0, lengthSig);
      index += lengthSig;

      if (!Crypto.VerifySignature(buffer, indexTxStart, lengthMessage, KeyPublic, signature))
        throw new ProtocolException($"TX {this} contains invalid signature.");
    }

    internal override void Serialize(Wallet wallet)
    {
      SerializeUnsigned(wallet.SHA256);

      byte[] signature = wallet.GetSignature(TXRaw);

      TXRaw = [.. TXRaw, (byte)signature.Length, .. signature];
    }

    internal void SerializeUnsigned(SHA256 sHA256)
    {
      List<byte> tXRaw = new();

      tXRaw.AddRange(KeyPublic);
      tXRaw.AddRange(BitConverter.GetBytes(BlockheightAccountCreated));
      tXRaw.AddRange(BitConverter.GetBytes(Nonce));
      tXRaw.AddRange(BitConverter.GetBytes(Fee));

      SerializeTXOutputs(tXRaw);

      TXRaw = tXRaw.ToArray();

      Hash = sHA256.ComputeHash(sHA256.ComputeHash(TXRaw));
    }
  }
}

