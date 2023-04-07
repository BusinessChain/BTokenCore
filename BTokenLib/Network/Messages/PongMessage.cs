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
      public UInt64 Nonce;

      public PongMessage(MessageNetwork networkMessage)
        : base(
            "pong",
            networkMessage.Payload)
      {
        Nonce = BitConverter.ToUInt64(Payload, 0);
      }

      public PongMessage(ulong nonce) 
        : base("pong")
      {
        Nonce = nonce;
        Payload = BitConverter.GetBytes(nonce);
        LengthDataPayload = Payload.Length;
      }
    }
  }
}
