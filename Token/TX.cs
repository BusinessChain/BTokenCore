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
