using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  partial class Network
  {
    class PongMessage : MessageNetwork
    {
      public PongMessage(ulong nonce) 
        : base("pong")
      {
        Payload = BitConverter.GetBytes(nonce);
        LengthDataPayload = Payload.Length;
      }
    }
  }
}
