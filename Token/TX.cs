namespace BTokenCore;

public abstract class TX
{
  internal byte[] Hash;

  internal int CountBytes;

  internal long Fee;

  internal byte[] TXRaw;

  internal List<TXOutput> TXOutputs = new();


  internal abstract void Serialize(Wallet wallet);

  protected TXOutput ParseTXOutput(byte[] buffer, ref int startIndex)
  {
    long value = buffer.Read<long>(ref startIndex);

    int lengthScript = VarInt.GetInt(buffer, ref startIndex);
    int indexScript = startIndex;
    startIndex += lengthScript;

    TXOutput tXOutput;

    if (lengthScript == TXOutputP2PKH.LENGTH_SCRIPT_P2PKH &&
      TXOutputP2PKH.PREFIX_P2PKH.IsAllBytesEqual(buffer, indexScript) &&
      TXOutputP2PKH.POSTFIX_P2PKH.IsAllBytesEqual(buffer, indexScript + TXOutputP2PKH.LENGTH_SCRIPT_P2PKH - TXOutputP2PKH.POSTFIX_P2PKH.Length))
    {
      tXOutput = new TXOutputP2PKH(buffer, indexScript);
    }
    else if (lengthScript == TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN &&
      TXOutputTokenAnchor.PREFIX_ANCHOR_TOKEN.IsAllBytesEqual(buffer, indexScript))
    {
      tXOutput = new TXOutputTokenAnchor(buffer, ref indexScript);
    }
    else
      tXOutput = new TXOutput();

    tXOutput.Value = value;

    return tXOutput;
  }

  internal void SerializeTXOutputs(List<byte> tXRaw)
  {
    tXRaw.AddRange(VarInt.GetBytes(TXOutputs.Count));

    foreach (TXOutput output in TXOutputs)
    {
      tXRaw.AddRange(BitConverter.GetBytes(output.Value));
      tXRaw.AddRange(VarInt.GetBytes(output.Script.Length));
      tXRaw.AddRange(output.Script);
    }
  }

  internal double GetFeePerByte()
  {
    return (double)Fee / CountBytes;
  }

  internal long GetValueOutputs()
  {
    return TXOutputs.Sum(t => t.Value);
  }
}
