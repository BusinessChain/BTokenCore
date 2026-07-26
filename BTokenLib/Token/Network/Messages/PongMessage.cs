using System;
using System.Threading.Tasks;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      partial class Peer
      {
        class PongMessage : MessageNetworkProtocol
        {
          public const string Command = "pong";


          public PongMessage()
          { }

          public PongMessage(byte[] payload, int lengthDataPayload)
          {
            Payload = payload;
            LengthDataPayload = lengthDataPayload;
          }

          public override async Task Run(Peer peer)
          {
            PingMessage messagePing = peer.ProtocolStateMachine[PingMessage.Command] as PingMessage;

            if (messagePing == null)
              throw new ProtocolException("Transistion into state 'pong' from other than state 'ping' is not supported.");

            if (messagePing.Payload != Payload)
              throw new ProtocolException("'Pong' message did not return same nonce as sended in 'ping' message.");

            peer.ProtocolStateMachine = null;
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