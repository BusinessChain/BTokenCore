using System.Net;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class Network
{
  abstract class NetworkMessage
  {
    internal byte[] Payload;
    internal int LengthDataPayload;

    protected DOSMonitorPer10Minutes DOSMonitor;


    internal NetworkMessage(byte[] payload, int maxLevelDoS, int amountDrainDoSPer10Minutes)
    {
      Payload = payload;
      LengthDataPayload = payload.Length;
      DOSMonitor = new DOSMonitorPer10Minutes(maxLevelDoS, amountDrainDoSPer10Minutes);
    }

    internal virtual byte[] GetPayloadBuffer()
    {
      return Payload;
    }

    internal virtual void IncrementDOSMonitor()
    {
      DOSMonitor.Increment(1);
    }

    internal abstract Task Run(Peer peer);

    internal abstract string GetCommand();
  }

  class AddressMessage : NetworkMessage
  {
    internal const string Command = "addr";

    internal List<NetworkAddress> NetworkAddresses = new();

    const int SIZE_BUFFER_PAYLOAD = 30_003;
    const int MAX_LEVEL_DOS = 10;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 10;


    internal AddressMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal AddressMessage(byte[] messagePayload)
      : base(messagePayload, MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
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

  class PingMessage : NetworkMessage
  {
    internal const string Command = "ping";

    internal UInt64 Nonce;


    const int SIZE_BUFFER_PAYLOAD = 8;
    const int MAX_LEVEL_DOS = 10;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 10;


    internal PingMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal PingMessage(byte[] payload)
      : base(payload, MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      await PongMessage.SendPong(peer, LengthDataPayload, Payload);
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

  class BlockMessage : NetworkMessage
  {
    internal const string Command = "block";

    internal Block BlockDownload;
    internal DateTime TimeRequestBlock;

    Blockchain Blockchain;

    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal BlockMessage(Blockchain blockchain, Block blockDownload)
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
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

      BlockDownload.LengthDataPayload = LengthDataPayload;
      BlockDownload.Parse();

      DOSMonitor.Decrement(1);

      HeadersMessage headersMessage = (HeadersMessage)peer.ProtocolStateMachine[HeadersMessage.Command];

      BlockDownload = await Blockchain.InsertBlockReturnBlockMissing(
        BlockDownload,
        headersMessage.HeaderTipReceivedLast,
        peer.CanDeliverBlock);

      if (BlockDownload.Header != null)
        await SendBlockRequest(peer, BlockDownload.Header);
      else
        peer.StateCurrent = Peer.StateProtocol.Idle;
    }

    internal async Task SendBlockRequest(Peer peer, Header header)
    {
      BlockDownload.Header = header;
      TimeRequestBlock = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.BlockDownload;

      await GetDataMessage.SendGetData(peer, [new(Inventory.InventoryType.MSG_BLOCK, header.Hash)]);
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

  class GetDataMessage : NetworkMessage
  {
    internal const string Command = "getdata";

    Blockchain Blockchain;

    internal Block BlockUpload;

    internal int HeightBlockDownloadedLast;

    const int SIZE_BUFFER_PAYLOAD = 36_003;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal GetDataMessage(Blockchain blockchain, Block blockUpload)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
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
          if (Blockchain.Token.TryGetTX(inventory.Hash, out TX tXInPool))
            TXMessage.Send(peer, tXInPool.TXRaw);
        }
        else if (inventory.Type == Inventory.InventoryType.MSG_BLOCK)
        {
          BlockUpload.Header = null;

          await Blockchain.GetBlock(inventory.Hash, BlockUpload);

          if (BlockUpload.Header != null)
          {
            BlockMessage.SendBlock(peer, BlockUpload);

            if (BlockUpload.Header.Height > HeightBlockDownloadedLast)
              DOSMonitor.Decrement(1);

            HeightBlockDownloadedLast = BlockUpload.Header.Height;
          }
          else
          {
            byte[] buffer = [.. VarInt.GetBytes(1), .. inventory.GetBytes()];
            await peer.SocketCommunication.SendMessage(NotFoundMessage.Command, buffer.Length, buffer);
          }
        }
        else if (inventory.Type == Inventory.InventoryType.MSG_DB)
        {
        }
      }
    }

    internal static async Task SendGetData(Peer peer, List<Inventory> inventories)
    {
      List<byte> payload = new();

      payload.AddRange(VarInt.GetBytes(inventories.Count));

      foreach (Inventory inventory in inventories)
        payload.AddRange(inventory.GetBytes());

      byte[] buffer = payload.ToArray();

      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

  class NotFoundMessage : NetworkMessage
  {
    internal const string Command = "notfound";

    const int SIZE_BUFFER_PAYLOAD = 36_003;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal NotFoundMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      VarInt.GetInt(Payload, ref startIndex);
      Inventory inventory = Inventory.Parse(Payload, ref startIndex);

      BlockMessage blockMessage = (BlockMessage)peer.ProtocolStateMachine[BlockMessage.Command];

      if (blockMessage.BlockDownload.Header?.Hash.IsAllBytesEqual(inventory.Hash) != true)
        return;

      DOSMonitor.Decrement(1);

      peer.HashesBlockRefused.Add(inventory.Hash);
      blockMessage.BlockDownload.Header = null;
      peer.StateCurrent = Peer.StateProtocol.Idle;
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

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
        await Blockchain.GetHeadersSerialized(hashesLocator, HeadersMessage.MAX_COUNT_HEADERS);

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

      payload.AddRange(BitConverter.GetBytes(peer.ConfigNetwork.ProtocolVersion));
      payload.AddRange(VarInt.GetBytes(locator.Count()));

      foreach (byte[] locatorHash in locator)
        payload.AddRange(locatorHash);

      payload.AddRange("0000000000000000000000000000000000000000000000000000000000000000".ToBinary());

      byte[] buffer = payload.ToArray();

      peer.StateCurrent = Peer.StateProtocol.HeaderDownload;

      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

  class HeadersMessage : NetworkMessage
  {
    internal const int MAX_COUNT_HEADERS = 2000;
    internal const string Command = "headers";

    const int SIZE_BUFFER_PAYLOAD = 3 + MAX_COUNT_HEADERS * 101;
    const int MAX_LEVEL_DOS = 20;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 2;

    Blockchain Blockchain;
    internal Header HeaderTipReceivedLast;

    SHA256 SHA256 = SHA256.Create();


    internal HeadersMessage(Blockchain blockchain)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
    }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;
      int countHeaders = VarInt.GetInt(Payload, ref startIndex);

      if (!peer.ConfigNetwork.IsProtocolBitcoin)
      {
        if (countHeaders == 1)
          peer.HashBlockAnnounced = Blockchain.Token.ParseHeader(Payload, ref startIndex, SHA256).Hash;

        return;
      }

      if (countHeaders > MAX_COUNT_HEADERS)
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
        headers.Add(Blockchain.Token.ParseHeader(Payload, ref startIndex, SHA256));
        VarInt.GetInt(Payload, ref startIndex);
      }

      Header headerTipReceivedLast = await Blockchain.TryInsertHeadersInTree(headers);

      if (headerTipReceivedLast != null)
        HeaderTipReceivedLast = headerTipReceivedLast;

      if (peer.StateCurrent == Peer.StateProtocol.HeaderDownload)
      {
        if (headerTipReceivedLast != null)
        {
          DOSMonitor.Decrement(1);
          GetHeadersMessage.SendGetHeaders(peer, [headerTipReceivedLast.Hash]);
        }
        else
          peer.StateCurrent = Peer.StateProtocol.Idle;
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

      bufferList.InsertRange(0, VarInt.GetBytes(headersSerialized.Count));

      byte[] buffer = bufferList.ToArray();

      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

  class InvMessage : NetworkMessage
  {
    internal const string Command = "inv";

    internal List<Inventory> Inventories = new();

    Blockchain Blockchain;

    const int SIZE_BUFFER_PAYLOAD = 36_003;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal InvMessage(Blockchain blockchain)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
    }

    internal InvMessage(List<Inventory> inventories)
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
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
      : base(buffer, MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
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
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

      for (int i = 0; i < inventoryCount; i++)
        if (Inventory.Parse(Payload, ref startIndex).Type == Inventory.InventoryType.MSG_BLOCK)
        {
          await GetHeadersMessage.SendGetHeaders(peer, await Blockchain.GetLocator());
          return;
        }
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

  class PongMessage : NetworkMessage
  {
    internal const string Command = "pong";

    const int SIZE_BUFFER_PAYLOAD = 8;
    const int MAX_LEVEL_DOS = 10;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 10;


    internal PongMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal PongMessage(byte[] payload, int lengthDataPayload)
      : base(payload, MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
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

  class TXMessage : NetworkMessage
  {
    internal const string Command = "tx";

    const int SIZE_BUFFER_PAYLOAD = 100_000;
    const int MAX_LEVEL_DOS = 1_000_000;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1_000_000;


    internal TXMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal TXMessage(byte[] tXRaw)
      : base(tXRaw, MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override void IncrementDOSMonitor()
    {
      DOSMonitor.Increment(LengthDataPayload);
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

  class VerAckMessage : NetworkMessage
  {
    internal const string Command = "verack";

    Blockchain Blockchain;

    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal VerAckMessage(Blockchain blockchain)
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
    }

    internal static async Task Send(Peer peer)
    {
      await peer.SocketCommunication.SendMessage(Command, 0, new byte[0]);
    }

    internal override async Task Run(Peer peer)
    {
      peer.StateCurrent = Peer.StateProtocol.Idle;

      if (peer.ConfigNetwork.IsProtocolBitcoin && peer.Connection == Peer.ConnectionType.OUTBOUND)
        await GetHeadersMessage.SendGetHeaders(peer, await Blockchain.GetLocator());
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

  class VersionMessage : NetworkMessage
  {
    internal const string Command = "version";

    Blockchain Blockchain;

    const int SIZE_BUFFER_PAYLOAD = 1_000;
    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal VersionMessage(Blockchain blockchain)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
    }

    internal static byte[] GetBytes(UInt16 uint16)
    {
      byte[] byteArray = BitConverter.GetBytes(uint16);
      Array.Reverse(byteArray);
      return byteArray;
    }

    internal static async Task SendVersion(Peer peer, int heightBlockchainTip)
    {
      ConfigNetwork c = peer.ConfigNetwork;

      byte[] buffer =
      [
        .. BitConverter.GetBytes(c.ProtocolVersion),
        .. BitConverter.GetBytes(c.NetworkServicesLocal),
        .. BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        .. BitConverter.GetBytes(c.NetworkServicesRemote),
        .. IPAddress.Loopback.GetAddressBytes(),
        .. GetBytes((ushort)c.Port),
        .. BitConverter.GetBytes(c.NetworkServicesLocal),
        .. IPAddress.Loopback.GetAddressBytes(),
        .. GetBytes((ushort)c.Port),
        .. BitConverter.GetBytes((ulong)0),
        .. VarString.GetBytes(c.UserAgent),
        .. BitConverter.GetBytes(heightBlockchainTip),
        c.EnableRelay ? (byte)0x01 : (byte)0x00,
      ];

      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }

    internal override async Task Run(Peer peer)
    {
      VerAckMessage.Send(peer);

      if (peer.Connection == Peer.ConnectionType.INBOUND)
        SendVersion(peer, Blockchain.GetHeight());
    }

    internal override string GetCommand()
    {
      return Command;
    }
  }

  class UnknownMessage : NetworkMessage
  {
    internal const string Command = "commandUnknown";

    const int SIZE_BUFFER_PAYLOAD = 4_000_000; // does this really have to be that big, claude?
    const int MAX_LEVEL_DOS = 100;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 100;


    internal UnknownMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {

    }

    internal override string GetCommand()
    {
      return Command;
    }
  }
}