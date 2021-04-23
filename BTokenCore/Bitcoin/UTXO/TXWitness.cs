using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace BTokenCore
{
  partial class UTXOTable
  {
    class TXWitness
    {
      public static TXWitness Parse(byte[] byteStream, ref int startIndex)
      {
        return new TXWitness();
      }
    }
  }
}
