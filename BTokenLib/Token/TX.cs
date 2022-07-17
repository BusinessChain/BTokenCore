using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public class TX
  {
    public byte[] Hash;

    public List<byte> TXRaw = new();

    public int TXIDShort;

    public List<TXInput> TXInputs = new();
    public List<TXOutput> TXOutputs = new();

    public long Fee;

    public string GetStringTXRaw()
    {
      return TXRaw.ToArray()
        .Reverse().ToArray()
        .ToHexString();
    }

    public override string ToString()
    {
      return Hash.ToHexString();
    }
  }
}
