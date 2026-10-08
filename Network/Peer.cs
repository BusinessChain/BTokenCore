namespace BTokenCore;

internal partial class Network
{
  class Peer
  {
    internal Dictionary<string, NetworkMessage> ProtocolStateMachine;

    internal ConfigNetwork ConfigNetwork;

    internal ISocketCommunication SocketCommunication;

    internal enum ConnectionType { OUTBOUND, INBOUND };
    internal ConnectionType Connection;

    internal enum StateProtocol
    {
      Handshake,
      Idle,
      HeaderDownload,
      BlockDownload,
      Disposed
    }

    internal StateProtocol StateCurrent = StateProtocol.Handshake;

    internal SemaphoreSlim SemaphorePeer = new(1);

    internal HashSet<byte[]> HashesBlockRefused = new(new EqualityComparerByteArray());

    internal byte[] HashBlockAnnounced;


    internal Peer(
      Network network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
    {
      ConfigNetwork = network.ConfigNetwork;

      ProtocolStateMachine = network.CreateStateMachineProtocol();
      SocketCommunication = socketCommunication;
      Connection = connection;
    }

    internal bool IsDisposed()
    {
      return StateCurrent == StateProtocol.Disposed;
    }

    internal bool CanDeliverBlock(Header header)
    {
      bool isRefusedByPeer = HashesBlockRefused.Contains(header.Hash);
      bool isAnnouncedByPeer = HashBlockAnnounced != null && header.Hash.IsAllBytesEqual(HashBlockAnnounced);
      bool hasBlockHadTimeToSpread = !header.IsParentNewest();

      return !isRefusedByPeer && (hasBlockHadTimeToSpread || isAnnouncedByPeer);
    }

    internal async Task Start(int heightBlockchainTip)
    {
      await SocketCommunication.Start();

      StartMessageReceiver();

      if (Connection == ConnectionType.OUTBOUND)
        VersionMessage.SendVersion(this, heightBlockchainTip);
    }

    internal void BroadcastTX(TX tX)
    {
      InvMessage invMessage = new(new List<Inventory> {
            new(Inventory.InventoryType.MSG_TX, tX.Hash)});

      SendMessage(invMessage);
    }

    internal async Task AdvertizeTX(TX tX)
    {
      InvMessage invMessage = new(new List<Inventory> {
          new(Inventory.InventoryType.MSG_TX, tX.Hash)
        });

      await SendMessage(invMessage);
    }

    internal string GetIP()
    {
      return SocketCommunication.GetIP();
    }

    async Task StartMessageReceiver()
    {
      try
      {
        while (true)
        {
          string commandMessage = await SocketCommunication.ReceiveCommandMessageNext();

          await SemaphorePeer.WaitAsync().ConfigureAwait(false);

          try
          {
            NetworkMessage message = ProtocolStateMachine.GetValueOrDefault(
              commandMessage,
              ProtocolStateMachine[UnknownMessage.Command]);

            message.LengthDataPayload = await SocketCommunication.ReceivePayloadNext(message.GetPayloadBuffer());

            message.IncrementDOSMonitor();

            await message.Run(this);
          }
          finally
          {
            SemaphorePeer.Release();
          }
        }
      }
      finally
      {
        StateCurrent = StateProtocol.Disposed;
        SocketCommunication.Dispose();
      }
    }

    async Task SendMessage(NetworkMessage message)
    {
      await SocketCommunication.SendMessage(message.GetCommand(), message.LengthDataPayload, message.Payload);
    }
  }
}
