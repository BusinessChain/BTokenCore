using System.Buffers.Binary;
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

    internal static async Task SendItems(Peer peer, string command, List<byte[]> items)
    {
      byte[] buffer = [.. VarInt.GetBytes(items.Count), .. items.SelectMany(i => i)];

      await peer.SocketCommunication.SendMessage(command, buffer.Length, buffer);
    }

    internal static async Task SendInventories(Peer peer, string command, List<Inventory> inventories)
    {
      await SendItems(peer, command, inventories.Select(i => i.GetBytes()).ToList());
    }
  }

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
      await peer.SocketCommunication.SendMessage(PongMessage.Command, LengthDataPayload, Payload);
    }
  }

  protected class BlockMessage : NetworkMessage
  {
    internal const string Command = "block";

    internal Block BlockDownload;
    internal DateTime TimeRequest;

    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal BlockMessage(Blockchain blockchain)
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
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

      await peer.Network.Blockchain.RunLocked(() => peer.Network.Blockchain.ReceiveBlock(BlockDownload));

      DOSMonitor.Decrement(1);

      await RequestBlockNext(peer);
    }

    internal virtual async Task RequestBlockNext(Peer peer)
    {
      BlockDownload.Header = await peer.Network.Blockchain.RunLocked(() =>
        peer.Network.Blockchain.FetchHeaderBlockMissingNext(peer.CanDeliverBlock));

      if (BlockDownload.Header == null)
      {
        peer.StateCurrent = Peer.StateProtocol.Idle;
        return;
      }

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.BlockDownload;

      await SendInventories(peer, GetDataMessage.Command, [new(Inventory.InventoryType.MSG_BLOCK, BlockDownload.Header.Hash)]);
    }
  }

  protected class GetDataMessage : NetworkMessage
  {
    internal const string Command = "getdata";

    Header HeaderBlockDownloadedTip;

    const int SIZE_BUFFER_PAYLOAD = 36_003;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;

    DOSMonitor DOSMonitorTX = new(
      TXMessage.MAX_LEVEL_DOS,
      TXMessage.AMOUNT_DRAIN_DOS_PER_10_MINUTES);


    internal GetDataMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

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
          TX tX = await peer.Network.Blockchain.RunLocked(() => peer.Network.Blockchain.GetTX(inventory.Hash));

          if (tX != null)
          {
            DOSMonitorTX.Increment(tX.TXRaw.Length);
            await peer.SocketCommunication.SendMessage(TXMessage.Command, tX.TXRaw.Length, tX.TXRaw);
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

          (Block blockUpload, bool isBlockBehindTipDownloaded) = await peer.Network.Blockchain.RunLocked(() =>
          {
            Block block = peer.Network.Blockchain.GetBlock(inventory.Hash);

            if (block == null)
              return (block, false);

            return (block, HeaderBlockDownloadedTip?.GetHeaderAncestor(block.Header.Height) == block.Header);
          });

          if (blockUpload != null)
          {
            await peer.SocketCommunication.SendMessage(BlockMessage.Command, blockUpload.LengthDataPayload, blockUpload.Buffer);

            if (!isBlockBehindTipDownloaded)
            {
              DOSMonitor.Decrement(1);
              HeaderBlockDownloadedTip = blockUpload.Header;
            }
          }
          else
            await SendInventories(peer, NotFoundMessage.Command, [inventory]);
        }
        else if (inventory.Type is Inventory.InventoryType.MSG_SEGMENT_HASHES or Inventory.InventoryType.MSG_SEGMENT)
          await ServeAccounts(peer, inventory);
      }

      if (inventoriesTXNotFound.Count > 0)
        await SendInventories(peer, NotFoundMessage.Command, inventoriesTXNotFound);
    }

    protected virtual Task ServeAccounts(Peer peer, Inventory inventory)
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

      BlockMessage blockMessage = (BlockMessage)peer.MessagesByCommand[BlockMessage.Command];

      if (blockMessage.BlockDownload.Header?.Hash.IsAllBytesEqual(inventory.Hash) != true)
        return;

      DOSMonitor.Decrement(1);

      peer.HashesBlockRefused.TryGetValue(inventory.Hash, out (int countRefusals, DateTime timeRefusedLast) refusal);
      peer.HashesBlockRefused[inventory.Hash] = (refusal.countRefusals + 1, DateTime.UtcNow);

      blockMessage.BlockDownload.Header = null;
      peer.StateCurrent = Peer.StateProtocol.Idle;
    }
  }

  protected abstract class HeadersMessage : NetworkMessage
  {
    internal const int COUNT_MAX_HEADERS = 2000;
    internal const string Command = "headers";

    const int SIZE_BUFFER_PAYLOAD = 3 + COUNT_MAX_HEADERS * 101;
    const int MAX_LEVEL_DOS = 20;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 2;


    internal HeadersMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal static async Task SendHeaders(Peer peer, List<byte[]> headersSerialized)
    {
      await SendItems(peer, Command, headersSerialized.Select(byte[] (h) => [.. h, 0x00]).ToList());
    }
  }

  protected abstract class InvMessage : NetworkMessage
  {
    internal const string Command = "inv";

    internal const int COUNT_MAX_INVENTORIES = 100;

    const int SIZE_BUFFER_PAYLOAD = 3 + COUNT_MAX_INVENTORIES * 36;
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
      PingMessage messagePing = (PingMessage)peer.MessagesByCommand[PingMessage.Command];

      if (messagePing.IsPongPending
        && LengthDataPayload == SIZE_BUFFER_PAYLOAD
        && BitConverter.ToUInt64(Payload) == messagePing.Nonce)
        messagePing.IsPongPending = false;
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
  }

  protected class VerAckMessage : NetworkMessage
  {
    internal const string Command = "verack";

    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal VerAckMessage()
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      peer.StateCurrent = Peer.StateProtocol.Idle;
      peer.HandshakeCompleted.TrySetResult();
    }
  }

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