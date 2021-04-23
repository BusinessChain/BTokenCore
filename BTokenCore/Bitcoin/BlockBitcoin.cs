using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using BTokenLib;

namespace BTokenCore
{
  class BlockBitcoin : Block
  {
    public List<UTXOTable.TX> TXs = new();


    public BlockBitcoin(
      byte[] buffer,
      HeaderBitcoin header,
      List<UTXOTable.TX> tXs) : base(
        buffer,
        header)
    {
      TXs = tXs;
    }
  }
}
