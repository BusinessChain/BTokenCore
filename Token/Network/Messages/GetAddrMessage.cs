using System.Buffers.Binary;
using System.Net;

namespace BTokenCore;

internal partial class Network
{
  class GetAddrMessage : NetworkMessage
  {
    internal const string Command = "getaddr";

    const int COUNT_MAX_ADDRESSES = 1000;
    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal GetAddrMessage()
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override void IncrementDOSMonitor()
    { }

    internal override async Task Run(Peer peer)
    {
      if (!DOSMonitor.TryIncrement(1))
        return;

      List<string> iPs;

      lock (peer.Network.LOCK_Peers)
        iPs = peer.Network.IPAddresses.OrderBy(i => Random.Shared.Next()).Take(COUNT_MAX_ADDRESSES).ToList();

      Configuration c = peer.Network.ConfigurationNetwork;

      byte[] timeUnixSeconds = BitConverter.GetBytes((UInt32)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

      await SendItems(peer, AddressMessage.Command, iPs.Select(byte[] (iP) =>
      [
        .. timeUnixSeconds,
        .. BitConverter.GetBytes(c.NetworkServicesRemote),
        .. IPAddress.Parse(iP).MapToIPv6().GetAddressBytes(),
        .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((ushort)c.Port))
      ]).ToList());
    }
  }
}