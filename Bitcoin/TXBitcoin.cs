using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  internal class TXBitcoin : TX
  {
    internal List<TXInputBitcoin> Inputs = new();


    internal TXBitcoin()
    { }

    internal TXBitcoin(byte[] buffer, ref int index, bool isCoinbase)
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
        TXOutputs.Add(ParseTXOutput(buffer, ref index));
      }

      LockTime = buffer.Read<int>(ref index);

      IndexBuffer = indexTxStart;
      CountBytes = index - indexTxStart;

      Hash = Crypto.ComputeHash256(buffer.AsSpan(indexTxStart, index - indexTxStart));
    }

    internal override Dictionary<string, object> GetSnapshotState()
    {
      Dictionary<string, object> snapshotState = base.GetSnapshotState();

      snapshotState["inputs"] = Inputs.Select(i => new Dictionary<string, object>
      {
        ["tXIDOutput"] = i.TXIDOutput.ToHexString(),
        ["indexOutput"] = i.IndexOutput
      }).ToList();

      return snapshotState;
    }


    internal void Serialize(Wallet wallet, List<byte[]> iDsAccountInput)
    {
      List<byte> tXRaw = new();

      tXRaw.AddRange([0x01, 0x00, 0x00, 0x00]); // version

      tXRaw.Add((byte)Inputs.Count);

      foreach (TXInputBitcoin input in Inputs)
      {
        tXRaw.AddRange(input.TXIDOutput);
        tXRaw.AddRange(BitConverter.GetBytes(input.IndexOutput));
        tXRaw.Add(0x00); // length empty script
        tXRaw.AddRange(BitConverter.GetBytes(input.Sequence));
      }

      SerializeTXOutputs(tXRaw);

      tXRaw.AddRange(BitConverter.GetBytes(LockTime));
      tXRaw.AddRange([0x01, 0x00, 0x00, 0x00]); // sighash

      SignTX(tXRaw, wallet, iDsAccountInput);

      TXRaw = tXRaw.ToArray();

      Hash = Crypto.ComputeHash256(TXRaw);
    }

    void SignTX(List<byte> tXRaw, Wallet wallet, List<byte[]> iDsAccountInput)
    {
      List<List<byte>> signaturesPerInput = new();
      int indexFirstInput = 5;

      for (int i = 0; i < Inputs.Count; i++)
      {
        byte[] publicScript = TXOutputP2PKH.PREFIX_P2PKH.Concat(iDsAccountInput[i]).Concat(TXOutputP2PKH.POSTFIX_P2PKH).ToArray();
        byte[] keyPublic = wallet.GetKeyPublic(iDsAccountInput[i]);

        List<byte> tXRawSign = tXRaw.ToList();
        int indexRawSign = indexFirstInput + 36 * (i + 1) + 5 * i;

        tXRawSign[indexRawSign++] = (byte)publicScript.Length;
        tXRawSign.InsertRange(indexRawSign, publicScript);

        byte[] message = SHA256.HashData(tXRawSign.ToArray());

        byte[] signature = wallet.GetSignature(iDsAccountInput[i], message);

        List<byte> scriptSig = new();

        scriptSig.Add((byte)(signature.Length + 1));
        scriptSig.AddRange(signature);
        scriptSig.Add(0x01);

        scriptSig.Add((byte)keyPublic.Length);
        scriptSig.AddRange(keyPublic);

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