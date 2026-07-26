using System;
using System.Threading.Tasks;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      class TXMessage : MessageNetworkProtocol
      {
        public const string Command = "tx";

        public TXMessage()
        {
          // amount bytes per 10 minutes
          DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5000000);

        }

        public TXMessage(byte[] tXRaw)
        {
          Payload = tXRaw;
          LengthDataPayload = Payload.Length;
        }

        public override async Task Run(Peer peer)
        {

        }

        public static async Task Send(Peer peer, byte[] buffer)
        {
          await peer.SendMessage(Command, buffer.Length, buffer);
        }

        public override string GetCommand()
        {
          return Command;
        }
      }
    }
  }
}
