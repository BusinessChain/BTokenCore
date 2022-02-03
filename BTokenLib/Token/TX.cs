using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public abstract class TX
  {
    public byte[] TXRaw;
    public byte[] Hash;

    public int TXIDShort;

    public List<TXInput> TXInputs = new();
    public List<TXOutput> TXOutputs = new();
  }
}
