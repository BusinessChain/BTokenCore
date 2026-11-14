namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  internal class TXBToken : TX
  {
    const int LENGTH_PUBKEYCOMPRESSED = 33;

    internal const int LENGTH_TX_COINBASE = LENGTH_PUBKEYCOMPRESSED
      + sizeof(int) + sizeof(int) + sizeof(long)
      + 1 + sizeof(long) + 1 + TXOutputP2PKH.LENGTH_SCRIPT_P2PKH
      + sizeof(int);

    /// <summary>
    /// The PublicKey specifies from which account the funds are sourced.
    /// </summary>
    internal byte[] KeyPublic = new byte[LENGTH_PUBKEYCOMPRESSED];

    /// <summary>
    /// IDAccountSource is derived from the PublicKey and is used to address the account in the database.
    /// </summary>
    internal byte[] IDAccountSource;

    /// <summary>
    /// In order for the transaction to be valid, the nonce must be equal to the nonce of the 
    /// account source.
    /// </summary>
    internal int Nonce;

    /// <summary>
    /// This has to match the block height at which the account source was created.
    /// It is in a sense an extension of the nonce.
    /// </summary>
    internal int HeightAccountCreated;


    internal TXBToken()
    { }

    internal TXBToken(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures, byte[] iDToken)
    {
      int indexTxStart = index;

      KeyPublic = buffer.ReadBytes(ref index, LENGTH_PUBKEYCOMPRESSED);

      IDAccountSource = Crypto.ComputeHash160(KeyPublic);

      HeightAccountCreated = buffer.Read<int>(ref index);

      Nonce = buffer.Read<int>(ref index);

      Fee = buffer.Read<long>(ref index);

      if (Fee < 0 || Fee > SUPPLY_MAX)
        throw new ProtocolException($"Fee {Fee} of TX out of range.");

      long valueTX = Fee;

      int countOutputs = VarInt.GetInt(buffer, ref index);

      for (int i = 0; i < countOutputs; i++)
      {
        int indexOutput = index;

        TXOutput tXOutput = ParseTXOutput(buffer, ref index);

        if (tXOutput.GetType() == typeof(TXOutput))
          throw new ProtocolException("TX output script is neither P2PKH nor token anchor.");

        if (tXOutput is TXOutputTokenAnchor && BitConverter.ToInt64(buffer, indexOutput) != 0)
          throw new ProtocolException("Value of token anchor output not zero.");

        if (tXOutput is TXOutputP2PKH tXOutputP2PKH)
        {
          if (tXOutputP2PKH.Value < 0 || tXOutputP2PKH.Value > SUPPLY_MAX - valueTX)
            throw new ProtocolException($"Value {tXOutputP2PKH.Value} of TX output out of range.");

          valueTX += tXOutputP2PKH.Value;
        }

        TXOutputs.Add(tXOutput);
      }

      LockTime = buffer.Read<int>(ref index);

      Hash = Crypto.ComputeHash256(buffer.AsSpan(indexTxStart, index - indexTxStart));

      if (!isCoinbase)
      {
        int lengthMessage = index - indexTxStart;

        int lengthSig = buffer[index++];
        byte[] signature = buffer.ReadBytes(ref index, lengthSig);

        if (verifySignatures)
        {
          byte[] message = [.. iDToken, .. buffer.AsSpan(indexTxStart, lengthMessage)];

          if (!Crypto.VerifySignature(message, 0, message.Length, KeyPublic, signature))
            throw new ProtocolException($"TX {this} contains invalid signature.");
        }
      }

      CountBytes = index - indexTxStart;

      TXRaw = buffer[indexTxStart..index];
    }

    internal bool IsLockTimeValid(int heightBlock)
    {
      return !TXOutputs.Any(o => o is TXOutputTokenAnchor) || LockTime == heightBlock - 1;
    }

    internal void Serialize(Wallet wallet, byte[] iDToken)
    {
      SerializeUnsigned();

      byte[] signature = wallet.GetSignature(Crypto.ComputeHash160(KeyPublic), [.. iDToken, .. TXRaw]);

      TXRaw = [.. TXRaw, (byte)signature.Length, .. signature];
    }

    internal void SerializeUnsigned()
    {
      List<byte> tXRaw =
      [
        .. KeyPublic,
        .. BitConverter.GetBytes(HeightAccountCreated),
        .. BitConverter.GetBytes(Nonce),
        .. BitConverter.GetBytes(Fee),
      ];

      SerializeTXOutputs(tXRaw);

      tXRaw.AddRange(BitConverter.GetBytes(LockTime));

      TXRaw = tXRaw.ToArray();

      Hash = Crypto.ComputeHash256(TXRaw);
    }
  }
}