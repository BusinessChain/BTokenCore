using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  partial class Network
  {
    class PingMessage : MessageNetwork
    {
      public UInt64 Nonce { get; private set; }


      public PingMessage(MessageNetwork networkMessage)
        : base(
            "ping", 
            networkMessage.Payload)
      {
        Nonce = BitConverter.ToUInt64(Payload, 0);
      }

      public PingMessage(UInt64 nonce) 
        : base("ping")
      {
        Nonce = nonce;
        Payload = BitConverter.GetBytes(nonce);
        LengthDataPayload = Payload.Length;
      }
    }
  }
}
