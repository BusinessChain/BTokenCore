namespace BTokenCore;

public abstract class TX
{
  internal byte[] Hash;

  internal int CountBytes;

  internal long Fee;

  internal int LockTime;

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
      tXOutput = new TXOutputP2PKH(value, buffer, indexScript);
    }
    else if (lengthScript == TXOutputTokenAnchor.LENGTH_SCRIPT_ANCHOR_TOKEN &&
      TXOutputTokenAnchor.PREFIX_ANCHOR_TOKEN.IsAllBytesEqual(buffer, indexScript))
    {
      tXOutput = new TXOutputTokenAnchor(buffer, ref indexScript);
    }
    else
      tXOutput = new TXOutput();

    return tXOutput;
  }

  internal void SerializeTXOutputs(List<byte> tXRaw)
  {
    tXRaw.AddRange(VarInt.GetBytes(TXOutputs.Count));

    foreach (TXOutput output in TXOutputs)
    {
      long value = output is TXOutputP2PKH outputP2PKH ? outputP2PKH.Value : 0;

      tXRaw.AddRange(BitConverter.GetBytes(value));
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
    return TXOutputs.OfType<TXOutputP2PKH>().Sum(t => t.Value);
  }
}
