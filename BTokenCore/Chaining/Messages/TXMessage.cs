using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore.Chaining
{
  class TXMessage : NetworkMessage
  {
    public TXMessage(byte[] tXRaw) : base("tx")
    {
      Payload = tXRaw;
    }
  }
}
