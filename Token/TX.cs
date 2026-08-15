using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;


namespace BTokenCore;

internal abstract class TX
{
  internal byte[] Hash;

  internal int CountBytes;

  internal long Fee;

  internal byte[] TXRaw;

  internal List<TXOutput> TXOutputs = new();


  internal abstract void Serialize(Wallet wallet);

  internal long GetValueOutputs()
  {
    return TXOutputs.Sum(t => t.Value);
  }
}
