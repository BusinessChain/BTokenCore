namespace BTokenCore;

internal partial class Network
{
  class AddressMessage : NetworkMessage
  {
    internal const string Command = "addr";

    const int LENGTH_ADDRESS = 30;
    const int SIZE_BUFFER_PAYLOAD = 30_003;
    const int MAX_LEVEL_DOS = 1000;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 60;


    internal AddressMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      DOSMonitor.Increment(MAX_LEVEL_DOS - 1);
    }

    internal async Task SendGetAddr(Peer peer)
    {
      DOSMonitor.Decrement(MAX_LEVEL_DOS);

      await peer.SocketCommunication.SendMessage(GetAddrMessage.Command, 0, Array.Empty<byte>());
    }

    internal override void IncrementDOSMonitor()
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int addressesCount = VarInt.GetInt(Payload, ref startIndex);

      if ((long)addressesCount * LENGTH_ADDRESS > LengthDataPayload - startIndex)
        throw new ProtocolException($"Address count {addressesCount} exceeds the payload length {LengthDataPayload}.");

      if (!DOSMonitor.TryIncrement(addressesCount))
        return;

      List<string> iPs = new();

      for (int i = 0; i < addressesCount; i++)
      {
        Address address = Address.ParseAddress(Payload, ref startIndex);

        if (address.Port == peer.Network.ConfigurationNetwork.Port)
          iPs.Add((address.IPAddress.IsIPv4MappedToIPv6 ? address.IPAddress.MapToIPv4() : address.IPAddress).ToString());
      }

      lock (peer.Network.LOCK_Peers)
        peer.Network.IPAddresses.UnionWith(iPs.Take(COUNT_MAX_IP_ADDRESSES - peer.Network.IPAddresses.Count));
    }
  }
}