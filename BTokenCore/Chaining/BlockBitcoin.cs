using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore.Chaining
{
  class BlockBitcoin : Blockchain.Block
  {
    public List<UTXOTable.TX> TXs = new List<UTXOTable.TX>();


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
