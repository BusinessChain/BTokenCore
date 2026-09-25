using System;
using System.Text;
using System.Net;
using System.Threading.Tasks;
using System.Security.Cryptography;


namespace BTokenCore;

internal abstract class MessageNetworkProtocol
{
  internal byte[] Payload;
  internal int LengthDataPayload;

  internal DOSMonitorPer10Minutes DOSMonitor;


  internal MessageNetworkProtocol(byte[] payload, int maxLevelDoSPer10Minutes)
  {
    Payload = payload;
    LengthDataPayload = payload.Length;
    DOSMonitor = new DOSMonitorPer10Minutes(maxLevelDoSPer10Minutes);
  }

  internal virtual byte[] GetPayloadBuffer()
  {
    return Payload;
  }

  internal abstract Task Run(Peer peer);

  internal abstract string GetCommand();
}

class AddressMessage : MessageNetworkProtocol
{
  internal const string Command = "addr";

  internal List<NetworkAddress> NetworkAddresses = new();

  // up to 1000 addresses of 30 bytes each, plus count
  const int SIZE_BUFFER_PAYLOAD = 30_003;


  internal AddressMessage()
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 10)
  { }

  internal AddressMessage(byte[] messagePayload)
    : base(messagePayload, maxLevelDoSPer10Minutes: 0)
  {
    int startIndex = 0;

    int addressesCount = VarInt.GetInt(
      Payload,
      ref startIndex);

    for (int i = 0; i < addressesCount; i++)
    {
      NetworkAddress address = NetworkAddress.ParseAddress(
          Payload, ref startIndex);

      if (NetworkAddresses.Any(
        a => a.IPAddress.ToString() == address.IPAddress.ToString()))
        throw new ProtocolException("Duplicate network address advertized.");

      NetworkAddresses.Add(address);
    }
  }

  internal override async Task Run(Peer peer)
  {

  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class PingMessage : MessageNetworkProtocol
{
  internal const string Command = "ping";

  internal UInt64 Nonce;


  // nonce
  const int SIZE_BUFFER_PAYLOAD = 8;


  internal PingMessage()
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 10)
  { }

  internal PingMessage(byte[] payload)
    : base(payload, maxLevelDoSPer10Minutes: 0)
  { }

  internal override async Task Run(Peer peer)
  {
    PongMessage.SendPong(peer, LengthDataPayload, Payload);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class BlockMessage : MessageNetworkProtocol
{
  internal const string Command = "block";

  internal Block BlockDownload;

  Network Network;


  // The payload is read into BlockDownload.Buffer, see GetPayloadBuffer().
  internal BlockMessage(Network network, Block blockDownload)
    : base(Array.Empty<byte>(), maxLevelDoSPer10Minutes: 5)
  {
    Network = network;
    BlockDownload = blockDownload;
  }

  internal override byte[] GetPayloadBuffer()
  {
    return BlockDownload.Buffer;
  }

  internal override async Task Run(Peer peer)
  {
    if (BlockDownload?.Header == null)
      throw new ProtocolException($"Received unrequested block message.");

    DOSMonitor.Decrement(1);

    BlockDownload.LengthDataPayload = LengthDataPayload;
    BlockDownload.Parse();

    BlockDownload = await Network.InsertBlockReturnNextBlock(BlockDownload);

    if (BlockDownload.Header != null)
      GetDataMessage.SendBlockRequest(peer, BlockDownload.Header.Hash);
  }

  internal static async Task SendBlock(Peer peer, Block block)
  {
    await peer.SocketCommunication.SendMessage(Command, block.LengthDataPayload, block.Buffer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class GetDataMessage : MessageNetworkProtocol
{
  internal const string Command = "getdata";

  Network Network;

  internal Block BlockUpload;

  internal int HeightBlockDownloadedLast;

  // up to 1000 inventories of 36 bytes each, plus count
  const int SIZE_BUFFER_PAYLOAD = 36_003;


  internal GetDataMessage(Network network, Block blockUpload)
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 5)
  {
    Network = network;
    BlockUpload = blockUpload;
  }

  internal override async Task Run(Peer peer)
  {
    int startIndex = 0;

    int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

    for (int i = 0; i < inventoryCount; i++)
    {
      Inventory inventory = Inventory.Parse(Payload, ref startIndex);

      if (inventory.Type == Inventory.InventoryType.MSG_TX)
      {
        if (Network.Token.TryGetTX(inventory.Hash, out TX tXInPool))
          TXMessage.Send(peer, tXInPool.TXRaw);
      }
      else if (inventory.Type == Inventory.InventoryType.MSG_BLOCK)
      {
        BlockUpload.Header = null;

        await Network.GetBlock(inventory.Hash, BlockUpload);

        if (BlockUpload.Header != null)
        {
          BlockMessage.SendBlock(peer, BlockUpload);

          if (BlockUpload.Header.Height > HeightBlockDownloadedLast)
            DOSMonitor.Decrement(1);

          HeightBlockDownloadedLast = BlockUpload.Header.Height;
        }
      }
      else if (inventory.Type == Inventory.InventoryType.MSG_DB)
      {
      }
    }
  }

  internal static async Task SendBlockRequest(Peer peer, byte[] hash)
  {
    List<byte> payload = new();

    payload.AddRange(VarInt.GetBytes(1));
    payload.AddRange(BitConverter.GetBytes((uint)Inventory.InventoryType.MSG_BLOCK));
    payload.AddRange(hash);

    byte[] buffer = payload.ToArray();

    await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class GetHeadersMessage : MessageNetworkProtocol
{
  internal const string Command = "getheaders";

  Network Network;

  internal int HeightAncestorSentLast;

  // version, count, up to 101 locator hashes and the stop hash
  const int SIZE_BUFFER_PAYLOAD = 3_300;


  internal GetHeadersMessage(Network network)
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 5)
  {
    Network = network;
  }

  internal override async Task Run(Peer peer)
  {
    int startIndex = 0;

    byte[] version = new byte[4];
    Array.Copy(Payload, startIndex, version, 0, version.Length);
    startIndex += version.Length;

    int countHeaderLocator = VarInt.GetInt(Payload, ref startIndex);

    if (countHeaderLocator > 101)
      throw new ProtocolException($"Too many ({countHeaderLocator}) headers in locator.");

    List<byte[]> hashesLocator = new();

    for (int i = 0; i < countHeaderLocator; i += 1)
    {
      byte[] hashLocator = new byte[32];
      Array.Copy(Payload, startIndex, hashLocator, 0, hashLocator.Length);
      startIndex += hashLocator.Length;

      hashesLocator.Add(hashLocator);
    }

    (List<byte[]> headers, int heightAncestor) tupleHeadersSerialized =
      await Network.GetHeadersSerialized( hashesLocator, HeadersMessage.MAX_COUNT_HEADERS);

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

    payload.AddRange(BitConverter.GetBytes(peer.ProtocolVersion));
    payload.AddRange(VarInt.GetBytes(locator.Count()));

    foreach (byte[] locatorHash in locator)
      payload.AddRange(locatorHash);

    payload.AddRange("0000000000000000000000000000000000000000000000000000000000000000".ToBinary());

    byte[] buffer = payload.ToArray();

    await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class HeadersMessage : MessageNetworkProtocol
{
  internal const int MAX_COUNT_HEADERS = 2000;
  internal const string Command = "headers";

  Network Network;

  // count plus MAX_COUNT_HEADERS headers of the largest header size (BToken, 100 bytes),
  // each followed by its transaction count (1 byte)
  const int SIZE_BUFFER_PAYLOAD = 3 + MAX_COUNT_HEADERS * 101;

  SHA256 SHA256 = SHA256.Create();


  internal HeadersMessage(Network network)
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 5)
  {
    Network = network;
  }

  internal override async Task Run(Peer peer)
  {
    BlockMessage blockMessage = (BlockMessage)peer.ProtocolStateMachine[BlockMessage.Command];
    if (blockMessage.BlockDownload.Header != null)
      return;

    List<Header> headers = new();
    int startIndex = 0;
    int countHeaders = VarInt.GetInt(Payload, ref startIndex);

    if (countHeaders > MAX_COUNT_HEADERS)
      throw new ProtocolException($"Too many headers {countHeaders} in headers message.");

    for (int i = 0; i < countHeaders; i++)
    {
      headers.Add(Network.Token.ParseHeader(Payload, ref startIndex, SHA256));
      VarInt.GetInt(Payload, ref startIndex);
    }

    (byte[] headerTipHash, Header headerBlockDownlad) =
      await Network.TryExtendHeaderchain(headers);

    if (headerBlockDownlad != null)
    {
      blockMessage.BlockDownload.Header = headerBlockDownlad;
      GetDataMessage.SendBlockRequest(peer, headerBlockDownlad.Hash);
    }
    else if (headerTipHash != null)
    {
      DOSMonitor.Decrement(1);
      GetHeadersMessage.SendGetHeaders(peer, [headerTipHash]);
    }
  }

  internal static async Task SendHeaders(Peer peer, List<byte[]> headersSerialized)
  {
    List<byte> bufferList = new();

    foreach (byte[] headerSerialized in headersSerialized)
    {
      bufferList.AddRange(headerSerialized);
      bufferList.Add(0x00);
    }

    bufferList.InsertRange(0, VarInt.GetBytes(bufferList.Count));

    byte[] buffer = bufferList.ToArray();

    await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class InvMessage : MessageNetworkProtocol
{
  internal const string Command = "inv";

  internal List<Inventory> Inventories = new();

  // up to 1000 inventories of 36 bytes each, plus count
  const int SIZE_BUFFER_PAYLOAD = 36_003;


  internal InvMessage()
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 50)
  { }

  internal InvMessage(List<Inventory> inventories)
    : base(Array.Empty<byte>(), maxLevelDoSPer10Minutes: 0)
  {
    Inventories = inventories;

    List<byte> payload = new();

    payload.AddRange(VarInt.GetBytes(inventories.Count));

    Inventories.ForEach(
      i => payload.AddRange(i.GetBytes()));

    Payload = payload.ToArray();
    LengthDataPayload = Payload.Length;
  }

  internal InvMessage(byte[] buffer)
    : base(buffer, maxLevelDoSPer10Minutes: 0)
  {
    int startIndex = 0;

    int inventoryCount = VarInt.GetInt(
      Payload,
      ref startIndex);

    for (int i = 0; i < inventoryCount; i++)
      Inventories.Add(Inventory.Parse(
        Payload,
        ref startIndex));
  }

  internal override async Task Run(Peer peer)
  {

  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class PongMessage : MessageNetworkProtocol
{
  internal const string Command = "pong";

  // nonce
  const int SIZE_BUFFER_PAYLOAD = 8;


  internal PongMessage()
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 10)
  { }

  internal PongMessage(byte[] payload, int lengthDataPayload)
    : base(payload, maxLevelDoSPer10Minutes: 0)
  {
    LengthDataPayload = lengthDataPayload;
  }

  internal override async Task Run(Peer peer)
  {
    PingMessage messagePing = peer.ProtocolStateMachine[PingMessage.Command] as PingMessage;

    if (messagePing == null)
      throw new ProtocolException("Transistion into state 'pong' from other than state 'ping' is not supported.");

    if (messagePing.Payload != Payload)
      throw new ProtocolException("'Pong' message did not return same nonce as sended in 'ping' message.");

    peer.ProtocolStateMachine = null;
  }

  internal static async Task SendPong(Peer peer, int payloadLength, byte[] payload)
  {
    await peer.SocketCommunication.SendMessage(Command, payloadLength, payload);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class TXMessage : MessageNetworkProtocol
{
  internal const string Command = "tx";

  // standard maximum transaction size
  const int SIZE_BUFFER_PAYLOAD = 100_000;


  // maxLevel is meant as amount of bytes per 10 minutes
  internal TXMessage()
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 5_000_000)
  { }

  internal TXMessage(byte[] tXRaw)
    : base(tXRaw, maxLevelDoSPer10Minutes: 0)
  { }

  internal override async Task Run(Peer peer)
  {

  }

  internal static async Task Send(Peer peer, byte[] buffer)
  {
    await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class VerAckMessage : MessageNetworkProtocol
{
  internal const string Command = "verack";

  Network Network;


  internal VerAckMessage(Network network)
    : base(Array.Empty<byte>(), maxLevelDoSPer10Minutes: 1)
  {
    Network = network;
  }

  internal static async Task Send(Peer peer)
  {
    await peer.SocketCommunication.SendMessage(Command, 0, new byte[0]);
  }

  internal override async Task Run(Peer peer)
  {
    if (peer.Connection == Network.ConnectionType.OUTBOUND)
      Network.StartHeaderSync(peer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class VersionMessage : MessageNetworkProtocol
{
  internal const string Command = "version";

  Network Network;

  // about 90 bytes of fixed fields plus a user agent of up to 256 bytes
  const int SIZE_BUFFER_PAYLOAD = 1_000;


  internal VersionMessage(Network network)
    : base(new byte[SIZE_BUFFER_PAYLOAD], maxLevelDoSPer10Minutes: 1)
  {
    Network = network;
  }

  internal static byte[] GetBytes(UInt16 uint16)
  {
    byte[] byteArray = BitConverter.GetBytes(uint16);
    Array.Reverse(byteArray);
    return byteArray;
  }

  internal static async Task SendVersion(Peer peer, int heightBlockchainTip)
  {
    List<byte> versionPayload = new();

    versionPayload.AddRange(BitConverter.GetBytes(peer.ProtocolVersion));
    versionPayload.AddRange(BitConverter.GetBytes(peer.NetworkServicesLocal));
    versionPayload.AddRange(BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    versionPayload.AddRange(BitConverter.GetBytes(peer.NetworkServicesRemote));
    versionPayload.AddRange(IPAddress.Loopback.GetAddressBytes());
    versionPayload.AddRange(GetBytes((ushort)peer.Port));
    versionPayload.AddRange(BitConverter.GetBytes(peer.NetworkServicesLocal));
    versionPayload.AddRange(IPAddress.Loopback.GetAddressBytes());
    versionPayload.AddRange(GetBytes((ushort)peer.Port));
    versionPayload.AddRange(BitConverter.GetBytes((ulong)0));
    versionPayload.AddRange(VarString.GetBytes(peer.UserAgent));
    versionPayload.AddRange(BitConverter.GetBytes(heightBlockchainTip));
    versionPayload.Add(peer.RelayOption);

    byte[] buffer = versionPayload.ToArray();

    await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
  }

  internal override async Task Run(Peer peer)
  {
    VerAckMessage.Send(peer);

    if (peer.Connection == Network.ConnectionType.INBOUND)
      SendVersion(peer, Network.BlockchainRoot.HeaderTip.Height);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}
