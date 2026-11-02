using System.Net;


namespace BTokenCore;

internal partial class Network
{
  protected abstract class NetworkMessage
  {
    internal byte[] Payload;
    internal int LengthDataPayload;

    protected DOSMonitor DOSMonitor;


    internal NetworkMessage(byte[] payload, int maxLevelDoS, int amountDrainDoSPer10Minutes)
    {
      Payload = payload;
      LengthDataPayload = payload.Length;
      DOSMonitor = new DOSMonitor(maxLevelDoS, amountDrainDoSPer10Minutes);
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

    internal static async Task SendInventories(Peer peer, string command, List<Inventory> inventories)
    {
      List<byte> payload = new();

      payload.AddRange(VarInt.GetBytes(inventories.Count));

      foreach (Inventory inventory in inventories)
        payload.AddRange(inventory.GetBytes());

      byte[] buffer = payload.ToArray();

      await peer.SocketCommunication.SendMessage(command, buffer.Length, buffer);
    }
  }

  class AddressMessage : NetworkMessage
  {
    internal const string Command = "addr";

    internal List<Address> NetworkAddresses = new();

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
        Address address = Address.ParseAddress(
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
  }

  class PingMessage : NetworkMessage
  {
    internal const string Command = "ping";

    internal UInt64 Nonce;
    internal bool IsPongPending;
    DateTime TimePingSent;


    const int SIZE_BUFFER_PAYLOAD = 8;
    const int MAX_LEVEL_DOS = 10;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 10;

    const int INTERVAL_PING_SECONDS = 120;
    const int TIMEOUT_PONG_SECONDS = 1200;


    internal PingMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal async Task Ping(Peer peer)
    {
      if (IsPongPending)
      {
        if (DateTime.UtcNow - TimePingSent > TimeSpan.FromSeconds(TIMEOUT_PONG_SECONDS))
          peer.SocketCommunication.Dispose();
      }
      else if (DateTime.UtcNow - TimePingSent > TimeSpan.FromSeconds(INTERVAL_PING_SECONDS))
      {
        Nonce = (UInt64)Random.Shared.NextInt64();
        TimePingSent = DateTime.UtcNow;
        IsPongPending = true;

        byte[] payload = BitConverter.GetBytes(Nonce);

        await peer.SocketCommunication.SendMessage(Command, payload.Length, payload);
      }
    }

    internal override async Task Run(Peer peer)
    {
      await PongMessage.SendPong(peer, LengthDataPayload, Payload);
    }
  }

  protected class BlockMessage : NetworkMessage
  {
    internal const string Command = "block";

    internal Block BlockDownload;
    internal DateTime TimeRequest;

    Blockchain Blockchain;

    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal BlockMessage(Blockchain blockchain)
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
      BlockDownload = new(blockchain.SizeBlockMax);
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

      if (await Blockchain.TryInsertBlock(BlockDownload))
        BlockDownload = Blockchain.TakeBlockFromPool();

      DOSMonitor.Decrement(1);

      await RequestBlockNext(peer);
    }

    internal virtual async Task RequestBlockNext(Peer peer)
    {
      BlockDownload.Header = await Blockchain.FetchHeaderBlockMissingNext(peer.CanDeliverBlock);

      if (BlockDownload.Header == null)
      {
        peer.StateCurrent = Peer.StateProtocol.Idle;
        return;
      }

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.BlockDownload;

      await SendInventories(peer, GetDataMessage.Command, [new(Inventory.InventoryType.MSG_BLOCK, BlockDownload.Header.Hash)]);
    }

    internal static async Task SendBlock(Peer peer, Block block)
    {
      await peer.SocketCommunication.SendMessage(Command, block.LengthDataPayload, block.Buffer);
    }
  }

  protected class GetDataMessage : NetworkMessage
  {
    internal const string Command = "getdata";

    Blockchain Blockchain;

    internal Block BlockUpload;

    internal int HeightBlockDownloadedLast;

    const int SIZE_BUFFER_PAYLOAD = 36_003;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;

    DOSMonitor DOSMonitorTX = new(
      TXMessage.MAX_LEVEL_DOS,
      TXMessage.AMOUNT_DRAIN_DOS_PER_10_MINUTES);


    internal GetDataMessage(Blockchain blockchain)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
      BlockUpload = new(blockchain.SizeBlockMax);
    }

    internal override void IncrementDOSMonitor()
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

      List<Inventory> inventoriesTXNotFound = new();

      for (int i = 0; i < inventoryCount; i++)
      {
        Inventory inventory = Inventory.Parse(Payload, ref startIndex);

        if (inventory.Type == Inventory.InventoryType.MSG_TX)
        {
          TX tX = await Blockchain.GetTX(inventory.Hash);

          if (tX != null)
          {
            DOSMonitorTX.Increment(tX.TXRaw.Length);
            await TXMessage.Send(peer, tX.TXRaw);
          }
          else
          {
            DOSMonitorTX.Increment(Inventory.LENGTH);
            inventoriesTXNotFound.Add(inventory);
          }
        }
        else if (inventory.Type == Inventory.InventoryType.MSG_BLOCK)
        {
          DOSMonitor.Increment(1);

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
            await SendInventories(peer, NotFoundMessage.Command, [inventory]);
        }
        else if (inventory.Type == Inventory.InventoryType.MSG_DB)
          await ServeDB(peer, inventory);
      }

      if (inventoriesTXNotFound.Count > 0)
        await SendInventories(peer, NotFoundMessage.Command, inventoriesTXNotFound);
    }

    protected virtual Task ServeDB(Peer peer, Inventory inventory)
    {
      return Task.CompletedTask;
    }
  }

  protected class NotFoundMessage : NetworkMessage
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
  }

  protected abstract class HeadersMessage : NetworkMessage
  {
    internal const int MAX_COUNT_HEADERS = 2000;
    internal const string Command = "headers";

    const int SIZE_BUFFER_PAYLOAD = 3 + MAX_COUNT_HEADERS * 101;
    const int MAX_LEVEL_DOS = 20;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 2;

    protected Blockchain Blockchain;


    internal HeadersMessage(Blockchain blockchain)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      Blockchain = blockchain;
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
  }

  protected abstract class InvMessage : NetworkMessage
  {
    internal const string Command = "inv";

    internal const int MAX_COUNT_INVENTORIES = 100;

    const int SIZE_BUFFER_PAYLOAD = 3 + MAX_COUNT_INVENTORIES * 36;
    const int MAX_LEVEL_DOS = 240;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 150;


    internal InvMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }
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

    internal override async Task Run(Peer peer)
    {
      PingMessage messagePing = (PingMessage)peer.ProtocolStateMachine[PingMessage.Command];

      if (messagePing.IsPongPending
        && LengthDataPayload == SIZE_BUFFER_PAYLOAD
        && BitConverter.ToUInt64(Payload) == messagePing.Nonce)
        messagePing.IsPongPending = false;
    }

    internal static async Task SendPong(Peer peer, int payloadLength, byte[] payload)
    {
      await peer.SocketCommunication.SendMessage(Command, payloadLength, payload);
    }
  }

  protected abstract class TXMessage : NetworkMessage
  {
    internal const string Command = "tx";

    const int SIZE_BUFFER_PAYLOAD = 100_000;
    internal const int MAX_LEVEL_ANNOUNCED = 1_000_000;
    const int MARGIN_LEVEL_DOS = 1_000_000;
    internal const int MAX_LEVEL_DOS = MAX_LEVEL_ANNOUNCED + MARGIN_LEVEL_DOS;
    internal const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1_000_000;


    internal TXMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal static async Task Send(Peer peer, byte[] buffer)
    {
      await peer.SocketCommunication.SendMessage(Command, buffer.Length, buffer);
    }
  }

  protected class VerAckMessage : NetworkMessage
  {
    internal const string Command = "verack";

    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal VerAckMessage()
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal static async Task Send(Peer peer)
    {
      await peer.SocketCommunication.SendMessage(Command, 0, new byte[0]);
    }

    internal override async Task Run(Peer peer)
    {
      peer.StateCurrent = Peer.StateProtocol.Idle;
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
      Configuration c = peer.ConfigurationNetwork;

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
      ulong networkServicesRequired = peer.ConfigurationNetwork.NetworkServicesRemote;

      if ((BitConverter.ToUInt64(Payload, 4) & networkServicesRequired) != networkServicesRequired)
        throw new ProtocolException($"Peer lacks network services {networkServicesRequired}.");

      VerAckMessage.Send(peer);

      if (peer.Connection == Peer.ConnectionType.INBOUND)
        SendVersion(peer, Blockchain.GetHeight());
    }
  }

  class UnknownMessage : NetworkMessage
  {
    internal const string Command = "commandUnknown";

    const int SIZE_BUFFER_PAYLOAD = 10_000;
    const int MAX_LEVEL_DOS = 100;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 100;


    internal UnknownMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {

    }
  }
}