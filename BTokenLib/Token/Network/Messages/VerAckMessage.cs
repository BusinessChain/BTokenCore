using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      class VerAckMessage : MessageNetworkProtocol
      {
        public const string Command = "verack";

        public VerAckMessage()
        { }

        public static async Task Send(Peer peer)
        {
          await peer.SendMessage(Command, 0, new byte[0]);
        }

        public override async Task Run(Peer peer)
        {
          if (peer.Connection == ConnectionType.OUTBOUND)
            peer.Network.StartHeaderSync(peer);
        }

        public override string GetCommand()
        {
          return Command;
        }
      }
    }
  }
}