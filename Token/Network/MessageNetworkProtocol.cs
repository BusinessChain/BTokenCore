using System;
using System.Text;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;


namespace BTokenCore;

public partial class NetworkToken
{
  abstract class MessageNetworkProtocol
  {
    public byte[] Payload;
    public int LengthDataPayload;

    public DOSMonitorPer10Minutes DOSMonitor;


    public MessageNetworkProtocol()
      : this(new byte[0])
    { }

    public MessageNetworkProtocol(byte[] payload)
    {

      Payload = payload;

      LengthDataPayload = payload.Length;
    }

    public virtual byte[] GetPayloadBuffer()
    {
      return Payload;
    }

    public abstract Task Run(Peer peer);

    public abstract string GetCommand();
  }


  class AddressMessage : MessageNetworkProtocol
  {
    const string Command = "addr";

    public List<NetworkAddress> NetworkAddresses = new();

    public AddressMessage()
    { }

    public AddressMessage(byte[] messagePayload)
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


    public override async Task Run(Peer peer)
    {

    }

    public override string GetCommand()
    {
      return Command;
    }
  }
  class PingMessage : MessageNetworkProtocol
  {
    public const string Command = "ping";

    public UInt64 Nonce;


    public PingMessage()
    { }

    public PingMessage(byte[] payload)
    {
      Payload = payload;
      LengthDataPayload = Payload.Length;
    }


    public override async Task Run(Peer peer)
    {
      peer.SendMessage(new PongMessage(Payload, LengthDataPayload));
    }

    public override string GetCommand()
    {
      return Command;
    }
  }
  class BlockMessage : MessageNetworkProtocol
  {
    public const string Command = "block";

    public Block BlockDownload;


    public BlockMessage(Block blockDownload)
      : base()
    {
      BlockDownload = blockDownload;
    }

    public override byte[] GetPayloadBuffer()
    {
      return BlockDownload.Buffer;
    }

    public override async Task Run(Peer peer)
    {
      if (BlockDownload?.Header == null)
        throw new ProtocolException($"Received unrequested block message.");

      DOSMonitor.Decrement(1);

      BlockDownload.LengthDataPayload = LengthDataPayload;

      BlockDownload.Parse();

      BlockDownload = await peer.Network.InsertBlock(BlockDownload);

      if (BlockDownload.Header != null)
        GetDataMessage.SendBlockRequest(peer, BlockDownload.Header.Hash);
    }

    public static async Task SendBlock(Peer peer, Block block)
    {
      await peer.SendMessage(Command, block.LengthDataPayload, block.Buffer);
    }

    public override string GetCommand()
    {
      return Command;
    }
  }
  class GetDataMessage : MessageNetworkProtocol
  {
    public const string Command = "getdata";

    Block BlockUpload;


    int HeightBlockDownloadedLast;


    public GetDataMessage(Block blockUpload)
      : base()
    {
      BlockUpload = blockUpload;

      DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5);
    }

    public override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

      for (int i = 0; i < inventoryCount; i++)
      {
        Inventory inventory = Inventory.Parse(Payload, ref startIndex);

        if (inventory.Type == InventoryType.MSG_TX)
        {
          if (peer.Network.Token.TryGetTX(inventory.Hash, out TX tXInPool))
            TXMessage.Send(peer, tXInPool.TXRaw);
        }
        else if (inventory.Type == InventoryType.MSG_BLOCK)
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
        else if (inventory.Type == InventoryType.MSG_DB)
        {
        }
      }
    }

    public static async Task SendBlockRequest(Peer peer, byte[] hash)
    {
      List<byte> payload = new();

      payload.AddRange(VarInt.GetBytes(1));
      payload.AddRange(BitConverter.GetBytes((uint)InventoryType.MSG_BLOCK));
      payload.AddRange(hash);

      byte[] buffer = payload.ToArray();

      await peer.SendMessage(Command, buffer.Length, buffer);
    }

    public override string GetCommand()
    {
      return Command;
    }
  }
  class GetHeadersMessage : MessageNetworkProtocol
  {
    public const string Command = "getheaders";

    int HeightAncestorSentLast;


    public GetHeadersMessage()
    {
    }

    public override async Task Run(Peer peer)
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
        await peer.Network.GetHeadersSerialized(
          hashesLocator,
          HeadersMessage.MAX_COUNT_HEADERS);

      HeadersMessage.SendHeaders(peer, tupleHeadersSerialized.headers);

      if (tupleHeadersSerialized.heightAncestor > HeightAncestorSentLast)
      {
        DOSMonitor.Decrement(1);
        HeightAncestorSentLast = tupleHeadersSerialized.heightAncestor;
      }
    }

    public static async Task SendGetHeaders(Peer peer, List<byte[]> locator)
    {
      List<byte> payload = new();

      payload.AddRange(BitConverter.GetBytes(peer.Network.Token.ProtocolVersion));
      payload.AddRange(VarInt.GetBytes(locator.Count()));

      foreach (byte[] locatorHash in locator)
        payload.AddRange(locatorHash);

      payload.AddRange("0000000000000000000000000000000000000000000000000000000000000000".ToBinary());

      byte[] buffer = payload.ToArray();

      await peer.SendMessage(Command, buffer.Length, buffer);
    }

    public override string GetCommand()
    {
      return Command;
    }
  }
  class HeadersMessage : MessageNetworkProtocol
  {
    public const string Command = "headers";

    public const int MAX_COUNT_HEADERS = 2000;

    Block BlockDownload;

    SHA256 SHA256 = SHA256.Create();


    public HeadersMessage(Block blockDownload)
    {
      BlockDownload = blockDownload;
      DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5);
    }

    public override async Task Run(Peer peer)
    {
      int startIndex = 0;
      int countHeaders = VarInt.GetInt(Payload, ref startIndex);

      if (countHeaders > MAX_COUNT_HEADERS)
        throw new ProtocolException($"Too many headers {countHeaders} in headers message.");
      else if (countHeaders > 0)
      {
        Header headerRoot = ParseHeaderchain(peer, countHeaders, ref startIndex);

        List<byte[]> headerslocator = await peer.Network.ExtendHeaderchain(
          headerRoot,
          BlockDownload);

        if (headerslocator != null)
        {
          DOSMonitor.Decrement(1);
          GetHeadersMessage.SendGetHeaders(peer, headerslocator);
        }
      }
      else if (countHeaders == 0 && BlockDownload.Header != null)
        GetDataMessage.SendBlockRequest(peer, BlockDownload.Header.Hash);
    }

    Header ParseHeaderchain(Peer peer, int countHeaders, ref int startIndex)
    {
      Header headerRoot = peer.Network.Token.ParseHeader(Payload, ref startIndex, SHA256);
      VarInt.GetInt(Payload, ref startIndex);

      Header headerTip = headerRoot;

      countHeaders -= 1;

      while (countHeaders > 0)
      {
        Header header = peer.Network.Token.ParseHeader(Payload, ref startIndex, SHA256);
        VarInt.GetInt(Payload, ref startIndex);

        header.AppendToHeader(headerTip);
        headerTip.HeaderNext = header;
        headerTip = header;

        countHeaders -= 1;
      }

      return headerRoot;
    }

    public static async Task SendHeaders(Peer peer, List<byte[]> headersSerialized)
    {
      List<byte> bufferList = new();

      foreach (byte[] headerSerialized in headersSerialized)
      {
        bufferList.AddRange(headerSerialized);
        bufferList.Add(0x00);
      }

      bufferList.InsertRange(0, VarInt.GetBytes(bufferList.Count));

      byte[] buffer = bufferList.ToArray();

      await peer.SendMessage(Command, buffer.Length, buffer);
    }

    public override string GetCommand()
    {
      return Command;
    }
  }
  class InvMessage : MessageNetworkProtocol
  {
    public const string Command = "inv";

    public List<Inventory> Inventories = new();

    public InvMessage()
    { }

    public InvMessage(List<Inventory> inventories)
    {
      Inventories = inventories;

      List<byte> payload = new();

      payload.AddRange(VarInt.GetBytes(inventories.Count));

      Inventories.ForEach(
        i => payload.AddRange(i.GetBytes()));

      Payload = payload.ToArray();
      LengthDataPayload = Payload.Length;
    }

    public InvMessage(byte[] buffer)
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

    public override async Task Run(Peer peer)
    {

    }

    public override string GetCommand()
    {
      return Command;
    }
  }
  class PongMessage : MessageNetworkProtocol
  {
    public const string Command = "pong";


    public PongMessage()
    { }

    public PongMessage(byte[] payload, int lengthDataPayload)
    {
      Payload = payload;
      LengthDataPayload = lengthDataPayload;
    }

    public override async Task Run(Peer peer)
    {
      PingMessage messagePing = peer.ProtocolStateMachine[PingMessage.Command] as PingMessage;

      if (messagePing == null)
        throw new ProtocolException("Transistion into state 'pong' from other than state 'ping' is not supported.");

      if (messagePing.Payload != Payload)
        throw new ProtocolException("'Pong' message did not return same nonce as sended in 'ping' message.");

      peer.ProtocolStateMachine = null;
    }

    public override string GetCommand()
    {
      return Command;
    }
  }
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
