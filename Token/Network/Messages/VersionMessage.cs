using System.Buffers.Binary;
using System.Net;

namespace BTokenCore;

internal partial class Network
{
  class VersionMessage : NetworkMessage
  {
    internal const string Command = "version";

    const int SIZE_BUFFER_PAYLOAD = 1_000;
    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal VersionMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal static async Task SendVersion(Peer peer, int heightBlockchainTip)
    {
      Configuration c = peer.Network.ConfigurationNetwork;

      byte[] buffer =
      [
        .. BitConverter.GetBytes(c.ProtocolVersion),
        .. BitConverter.GetBytes(c.NetworkServicesLocal),
        .. BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        .. BitConverter.GetBytes(c.NetworkServicesRemote),
        .. IPAddress.Parse(peer.GetIP()).MapToIPv6().GetAddressBytes(),
        .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((ushort)c.Port)),
        .. new byte[26],
        .. BitConverter.GetBytes((ulong)0),
        .. VarString.GetBytes(c.UserAgent),
        .. BitConverter.GetBytes(heightBlockchainTip),
        c.EnableRelay ? (byte)0x01 : (byte)0x00,
      ];

      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }

    internal override async Task Run(Peer peer)
    {
      ulong networkServicesRequired = peer.Network.ConfigurationNetwork.NetworkServicesRemote;

      if ((BitConverter.ToUInt64(Payload, 4) & networkServicesRequired) != networkServicesRequired)
        throw new ProtocolException($"Peer lacks network services {networkServicesRequired}.");

      peer.SocketCommunication.SendMessage(VerAckMessage.Command, 0, new byte[0]);

      if (peer.Connection == Peer.ConnectionType.INBOUND)
      {
        SendVersion(peer, await peer.Network.Blockchain.RunLocked(peer.Network.Blockchain.GetHeight));

        lock (peer.Network.LOCK_Peers)
          if (peer.Network.IPAddresses.Count < COUNT_MAX_IP_ADDRESSES)
            peer.Network.IPAddresses.Add(peer.GetIP());
      }
    }
  }
}