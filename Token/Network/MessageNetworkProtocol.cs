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


  internal MessageNetworkProtocol()
    : this(Array.Empty<byte>())
  { }

  internal MessageNetworkProtocol(byte[] payload)
  {
    Payload = payload;
    LengthDataPayload = payload.Length;
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

  internal AddressMessage()
  { }

  internal AddressMessage(byte[] messagePayload)
    : base(messagePayload)
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


  internal PingMessage()
  { }

  internal PingMessage(byte[] payload)
  {
    Payload = payload;
    LengthDataPayload = Payload.Length;
  }

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


  internal BlockMessage(Block blockDownload)
    : base()
  {
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

    BlockDownload = await peer.Network.InsertBlockReturnNewBlock(BlockDownload);

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

  internal Block BlockUpload;

  internal int HeightBlockDownloadedLast;


  internal GetDataMessage(Block blockUpload)
    : base()
  {
    BlockUpload = blockUpload;

    DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5);
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
        if (peer.Network.Token.TryGetTX(inventory.Hash, out TX tXInPool))
          TXMessage.Send(peer, tXInPool.TXRaw);
      }
      else if (inventory.Type == Inventory.InventoryType.MSG_BLOCK)
      {
        BlockUpload.Header = null;

        await peer.Network.GetBlock(inventory.Hash, BlockUpload);

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

  internal int HeightAncestorSentLast;


  internal GetHeadersMessage()
  {
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
      await peer.Network.GetHeadersSerialized( hashesLocator, HeadersMessage.MAX_COUNT_HEADERS);

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

    payload.AddRange(BitConverter.GetBytes(peer.Network.Token.ProtocolVersion));
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
  internal const string Command = "headers";

  internal const int MAX_COUNT_HEADERS = 2000;

  internal Block BlockDownload;

  SHA256 SHA256 = SHA256.Create();


  internal HeadersMessage(Block blockDownload)
  {
    BlockDownload = blockDownload;
    DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5);
  }


  Header HeaderDownload;

  internal override async Task Run(Peer peer)
  {
    int startIndex = 0;
    int countHeaders = VarInt.GetInt(Payload, ref startIndex);

    if (countHeaders > MAX_COUNT_HEADERS)
      throw new ProtocolException($"Too many headers {countHeaders} in headers message.");
    else if (countHeaders > 0)
    {
      Header headerRoot = ParseHeaderchain(peer.Network.Token, countHeaders, startIndex);

      if (await peer.Network.TryLockBlockchain(10000)) // evt. mit LOCK_Node arbeiten
        try
        {
          // evt. Hier mit BlockchainRoot.TryFindChain arbeiten
          peer.Network.BlockchainRoot.TryExtendHeaderchain(
            headerRoot,
            out List<byte[]> headerslocatorNext,
            out HeaderDownload);

          if (headerslocatorNext != null)
          {
            DOSMonitor.Decrement(1);
            GetHeadersMessage.SendGetHeaders(peer, headerslocatorNext);
          }
        }
        finally
        {
          peer.Network.ReleaseLockBlockchain();
        }
    }
    else if (countHeaders == 0 && HeaderDownload != null)
      GetDataMessage.SendBlockRequest(peer, HeaderDownload.Hash);
  }

  Header ParseHeaderchain(Token token, int countHeaders, int startIndex)
  {
    Header headerRoot = null;
    Header headerTip = null;

    do
    {
      Header header = token.ParseHeader(Payload, ref startIndex, SHA256);
      VarInt.GetInt(Payload, ref startIndex);

      if (headerRoot == null)
      {
        headerRoot = header;
        headerTip = header;
      }
      else
      {
        header.AppendToHeader(headerTip);
        headerTip.HeaderNext = header;
        headerTip = header;
      }

      countHeaders -= 1;
    } while (countHeaders > 0);

    return headerRoot;
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

  internal InvMessage()
  { }

  internal InvMessage(List<Inventory> inventories)
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
    : base(buffer)
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


  internal PongMessage()
  { }

  internal PongMessage(byte[] payload, int lengthDataPayload)
  {
    Payload = payload;
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

  internal TXMessage()
  {
    // amount bytes per 10 minutes
    DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5000000);

  }

  internal TXMessage(byte[] tXRaw)
  {
    Payload = tXRaw;
    LengthDataPayload = Payload.Length;
  }

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

  internal VerAckMessage()
  { }

  internal static async Task Send(Peer peer)
  {
    await peer.SocketCommunication.SendMessage(Command, 0, new byte[0]);
  }

  internal override async Task Run(Peer peer)
  {
    if (peer.Connection == Network.ConnectionType.OUTBOUND)
      peer.Network.StartHeaderSync(peer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}

class VersionMessage : MessageNetworkProtocol
{
  internal const string Command = "version";

  internal VersionMessage()
  { }

  internal static byte[] GetBytes(UInt16 uint16)
  {
    byte[] byteArray = BitConverter.GetBytes(uint16);
    Array.Reverse(byteArray);
    return byteArray;
  }

  internal static async Task SendVersion(Peer peer)
  {
    List<byte> versionPayload = new();

    versionPayload.AddRange(BitConverter.GetBytes(peer.Network.Token.ProtocolVersion));
    versionPayload.AddRange(BitConverter.GetBytes(peer.Network.Token.NetworkServicesLocal));
    versionPayload.AddRange(BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    versionPayload.AddRange(BitConverter.GetBytes(peer.Network.Token.NetworkServicesRemote));
    versionPayload.AddRange(IPAddress.Loopback.GetAddressBytes());
    versionPayload.AddRange(GetBytes((ushort)peer.Network.Token.Port));
    versionPayload.AddRange(BitConverter.GetBytes(peer.Network.Token.NetworkServicesLocal));
    versionPayload.AddRange(IPAddress.Loopback.GetAddressBytes());
    versionPayload.AddRange(GetBytes((ushort)peer.Network.Token.Port));
    versionPayload.AddRange(BitConverter.GetBytes((ulong)0));
    versionPayload.AddRange(VarString.GetBytes(peer.Network.Token.UserAgent));
    versionPayload.AddRange(BitConverter.GetBytes(peer.Network.BlockchainRoot.GetHeight()));
    versionPayload.Add(peer.Network.Token.RelayOption);

    byte[] buffer = versionPayload.ToArray();

    await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
  }

  internal override async Task Run(Peer peer)
  {
    VerAckMessage.Send(peer);

    if (peer.Connection == Network.ConnectionType.INBOUND)
      SendVersion(peer);
  }

  internal override string GetCommand()
  {
    return Command;
  }
}
