namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class GetHeadersMessage : NetworkMessage
  {
    internal const string Command = "getheaders";

    Blockchain Blockchain;

    internal int HeightAncestorSentLast;

    const int SIZE_BUFFER_PAYLOAD = 3_300;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal GetHeadersMessage(Blockchain blockchain)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
    }

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
        await Blockchain.RunLocked(() => Blockchain.GetHeadersSerialized(hashesLocator, HeadersMessage.COUNT_MAX_HEADERS));

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

      payload.AddRange(BitConverter.GetBytes(peer.ConfigurationNetwork.ProtocolVersion));
      payload.AddRange(VarInt.GetBytes(locator.Count()));

      foreach (byte[] locatorHash in locator)
        payload.AddRange(locatorHash);

      payload.AddRange("0000000000000000000000000000000000000000000000000000000000000000".ToBinary());

      byte[] buffer = payload.ToArray();

      peer.StateCurrent = Peer.StateProtocol.HeaderDownload;

      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }
  }

  class HeadersMessageBitcoin : HeadersMessage
  {
    internal HeadersMessageBitcoin(Blockchain blockchain)
      : base(blockchain)
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;
      int countHeaders = VarInt.GetInt(Payload, ref startIndex);

      if (countHeaders > COUNT_MAX_HEADERS)
        throw new ProtocolException($"Too many headers {countHeaders} in headers message.");

      if (countHeaders == 0)
      {
        if (peer.StateCurrent == Peer.StateProtocol.HeaderDownload)
          peer.StateCurrent = Peer.StateProtocol.Idle;

        return;
      }

      List<Header> headers = new();

      for (int i = 0; i < countHeaders; i++)
      {
        headers.Add(Blockchain.ParseHeader(Payload, ref startIndex));
        VarInt.GetInt(Payload, ref startIndex);
      }

      Header headerTipReceivedLast = await Blockchain.RunLocked(() => Blockchain.TryInsertHeadersInTree(headers));

      if (headerTipReceivedLast != null)
        ((PeerBitcoin)peer).HeaderTipReceivedLast = headerTipReceivedLast;

      if (peer.StateCurrent == Peer.StateProtocol.HeaderDownload)
      {
        if (headerTipReceivedLast != null)
        {
          DOSMonitor.Decrement(1);
          await GetHeadersMessage.SendGetHeaders(peer, [headerTipReceivedLast.Hash]);
        }
        else
          peer.StateCurrent = Peer.StateProtocol.Idle;
      }
    }
  }

  class InvMessageBitcoin : InvMessage
  {
    Blockchain Blockchain;


    internal InvMessageBitcoin(Blockchain blockchain)
    {
      Blockchain = blockchain;
    }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

      for (int i = 0; i < inventoryCount; i++)
        if (Inventory.Parse(Payload, ref startIndex).Type == Inventory.InventoryType.MSG_BLOCK)
        {
          await GetHeadersMessage.SendGetHeaders(peer, await Blockchain.RunLocked(Blockchain.GetLocator));
          return;
        }
    }
  }

  class VerAckMessageBitcoin : VerAckMessage
  {
    Blockchain Blockchain;


    internal VerAckMessageBitcoin(Blockchain blockchain)
    {
      Blockchain = blockchain;
    }

    internal override async Task Run(Peer peer)
    {
      await base.Run(peer);

      if (peer.Connection == Peer.ConnectionType.OUTBOUND)
        await GetHeadersMessage.SendGetHeaders(peer, await Blockchain.RunLocked(Blockchain.GetLocator));
    }
  }
}
