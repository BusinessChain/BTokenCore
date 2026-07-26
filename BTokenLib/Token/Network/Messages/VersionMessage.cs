using System;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      class VersionMessage : MessageNetworkProtocol
      {
        public const string Command = "version";

        public VersionMessage()
        { }

        static byte[] GetBytes(UInt16 uint16)
        {
          byte[] byteArray = BitConverter.GetBytes(uint16);
          Array.Reverse(byteArray);
          return byteArray;
        }

        public static async Task SendVersion(Peer peer)
        {
          List<byte> versionPayload = new();

          versionPayload.AddRange(BitConverter.GetBytes(peer.Network.ProtocolVersion));
          versionPayload.AddRange(BitConverter.GetBytes(peer.Network.NetworkServicesLocal));
          versionPayload.AddRange(BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
          versionPayload.AddRange(BitConverter.GetBytes(peer.Network.NetworkServicesRemote));
          versionPayload.AddRange(IPAddress.Loopback.GetAddressBytes());
          versionPayload.AddRange(GetBytes((ushort)peer.Network.Port));
          versionPayload.AddRange(BitConverter.GetBytes(peer.Network.NetworkServicesLocal));
          versionPayload.AddRange(IPAddress.Loopback.GetAddressBytes());
          versionPayload.AddRange(GetBytes((ushort)peer.Network.Port));
          versionPayload.AddRange(BitConverter.GetBytes((ulong)0));
          versionPayload.AddRange(VarString.GetBytes(peer.Network.UserAgent));
          versionPayload.AddRange(BitConverter.GetBytes(peer.Network.BlockchainRoot.GetHeight()));
          versionPayload.Add(peer.Network.RelayOption);

          byte[] buffer = versionPayload.ToArray();

          await peer.SendMessage(Command, buffer.Length, buffer);
        }

        public override async Task Run(Peer peer)
        {
          VerAckMessage.Send(peer);

          if (peer.Connection == ConnectionType.INBOUND)
            SendVersion(peer);
        }

        public override string GetCommand()
        {
          return Command;
        }
      }
    }
  }
}
