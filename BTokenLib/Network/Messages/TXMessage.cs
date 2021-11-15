using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  partial class Network
  {
    public class TXMessage : NetworkMessage
    {
      public TXMessage(byte[] tXRaw) 
        : base("tx")
      {
        Payload = tXRaw;
        LengthPayload = Payload.Length;
      }
    }
  }
}
