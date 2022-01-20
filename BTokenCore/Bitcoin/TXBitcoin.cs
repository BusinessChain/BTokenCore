using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using BTokenLib;

namespace BTokenCore
{
  class TXBitcoin : TX
  {
    public List<UTXOTable.TXInput> TXInputs = new();
    public List<UTXOTable.TXOutput> TXOutputs = new();
  }
}
