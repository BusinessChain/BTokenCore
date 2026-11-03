namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
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

    internal TXBToken(byte[] buffer, ref int index, bool flagIsCoinbase)
    {
      int indexTxStart = index;

      KeyPublic = buffer.ReadBytes(ref index, LENGTH_PUBKEYCOMPRESSED);

      IDAccountSource = Crypto.ComputeHash160(KeyPublic);

      BlockheightAccountCreated = buffer.Read<int>(ref index);

      Nonce = buffer.Read<int>(ref index);

      Fee = buffer.Read<long>(ref index);

      if (Fee < 0)
        throw new ProtocolException($"Fee {Fee} of TX smaller than zero.");

      int countOutputs = VarInt.GetInt(buffer, ref index);

      for (int i = 0; i < countOutputs; i++)
      {
        TXOutput tXOutput = ParseTXOutput(buffer, ref index);

        if (tXOutput.GetType() == typeof(TXOutput))
          throw new ProtocolException("TX output script is neither P2PKH nor token anchor.");

        if (tXOutput.Value < 0)
          throw new ProtocolException($"Value {tXOutput.Value} of TX output smaller than zero.");

        TXOutputs.Add(tXOutput);
      }

      Hash = Crypto.ComputeHash256(buffer.AsSpan(indexTxStart, index - indexTxStart));

      if (!flagIsCoinbase)
        VerifySignatureTX(indexTxStart, buffer, ref index);

      CountBytes = index - indexTxStart;

      TXRaw = buffer[indexTxStart..index];
    }

    internal void VerifySignatureTX(int indexTxStart, byte[] buffer, ref int index)
    {
      int lengthMessage = index - indexTxStart;

      int lengthSig = buffer[index++];
      byte[] signature = buffer.ReadBytes(ref index, lengthSig);

      if (!Crypto.VerifySignature(buffer, indexTxStart, lengthMessage, KeyPublic, signature))
        throw new ProtocolException($"TX {this} contains invalid signature.");
    }

    internal override void Serialize(Wallet wallet)
    {
      SerializeUnsigned();

      byte[] signature = wallet.GetSignature(TXRaw);

      TXRaw = [.. TXRaw, (byte)signature.Length, .. signature];
    }

    internal void SerializeUnsigned()
    {
      List<byte> tXRaw = new();

      tXRaw.AddRange(KeyPublic);
      tXRaw.AddRange(BitConverter.GetBytes(BlockheightAccountCreated));
      tXRaw.AddRange(BitConverter.GetBytes(Nonce));
      tXRaw.AddRange(BitConverter.GetBytes(Fee));

      SerializeTXOutputs(tXRaw);

      TXRaw = tXRaw.ToArray();

      Hash = Crypto.ComputeHash256(TXRaw);
    }
  }
}

