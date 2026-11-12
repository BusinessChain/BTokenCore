namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class GetHeadersMessage : NetworkMessage
  {
    internal const string Command = "getheaders";

    internal int HeightAncestorSentLast;

    const int SIZE_BUFFER_PAYLOAD = 3_300;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal GetHeadersMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      byte[] version = Payload.ReadBytes(ref startIndex, 4);

      int countHeaderLocator = VarInt.GetInt(Payload, ref startIndex);

      if (countHeaderLocator > 101)
        throw new ProtocolException($"Too many ({countHeaderLocator}) headers in locator.");

      List<byte[]> hashesLocator = new();

      for (int i = 0; i < countHeaderLocator; i += 1)
        hashesLocator.Add(Payload.ReadBytes(ref startIndex, Crypto.LENGTH_HASH256));

      (List<byte[]> headers, int heightAncestor) tupleHeadersSerialized =
        await peer.Network.Blockchain.RunLocked(() => peer.Network.Blockchain.GetHeadersSerialized(hashesLocator, HeadersMessage.COUNT_MAX_HEADERS));

      HeadersMessage.SendHeaders(peer, tupleHeadersSerialized.headers);

      if (tupleHeadersSerialized.heightAncestor > HeightAncestorSentLast)
      {
        DOSMonitor.Decrement(1);
        HeightAncestorSentLast = tupleHeadersSerialized.heightAncestor;
      }
    }

    internal static async Task SendGetHeaders(Peer peer, List<byte[]> locator)
    {
      List<byte> payload = new();

      payload.AddRange(BitConverter.GetBytes(peer.Network.ConfigurationNetwork.ProtocolVersion));
      payload.AddRange(VarInt.GetBytes(locator.Count()));

      foreach (byte[] locatorHash in locator)
        payload.AddRange(locatorHash);

      payload.AddRange("0000000000000000000000000000000000000000000000000000000000000000".ToBinary());

      byte[] buffer = payload.ToArray();

      peer.StateCurrent = Peer.StateProtocol.HeaderDownload;
      peer.TimeRequestHeaders = DateTime.UtcNow;

      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }
  }
}
