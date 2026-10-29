using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;


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
    long value = BitConverter.ToInt64(buffer, startIndex);
    startIndex += 8;

    int lengthScript = VarInt.GetInt(buffer, ref startIndex);
    int indexScript = startIndex;
    startIndex += lengthScript;

    TXOutput tXOutput;

    if (lengthScript == Token.LENGTH_SCRIPT_P2PKH &&
      Token.PREFIX_P2PKH.IsAllBytesEqual(buffer, indexScript) &&
      Token.POSTFIX_P2PKH.IsAllBytesEqual(buffer, indexScript + Token.LENGTH_SCRIPT_P2PKH - Token.POSTFIX_P2PKH.Length))
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

  internal long GetValueOutputs()
  {
    return TXOutputs.Sum(t => t.Value);
  }
}
