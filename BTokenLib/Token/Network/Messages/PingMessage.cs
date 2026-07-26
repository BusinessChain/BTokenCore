using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      partial class Peer
      {
        class PingMessage : MessageNetworkProtocol
        {
          public const string Command = "ping";

          public UInt64 Nonce;


          public PingMessage()
          { }

          public PingMessage(byte[] payload)
          {
            Payload = payload;
            LengthDataPayload = Payload.Length;
          }


          public override async Task Run(Peer peer)
          {
            peer.SendMessage(new PongMessage(Payload, LengthDataPayload));
          }

          public override string GetCommand()
          {
            return Command;
          }
        }
      }
    }
  }
}
