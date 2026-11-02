namespace BTokenCore;

internal partial class Network
{
  protected abstract class Peer
  {
    const int TIMESPAN_HEARTBEAT_MILLISECONDS = 1000;
    protected const int TIMEOUT_REQUEST_SECONDS = 60;

    internal Dictionary<string, NetworkMessage> ProtocolStateMachine;

    internal Configuration ConfigurationNetwork;

    internal ISocketCommunication SocketCommunication;

    internal enum ConnectionType { OUTBOUND, INBOUND };
    internal ConnectionType Connection;

    internal enum StateProtocol
    {
      Handshake,
      Idle,
      HeaderDownload,
      BlockDownload,
      DBDownload,
      Disposed
    }

    internal StateProtocol StateCurrent = StateProtocol.Handshake;

    internal SemaphoreSlim SemaphorePeer = new(1);

    internal HashSet<byte[]> HashesBlockRefused = new(new EqualityComparerByteArray());


    protected Peer(
      Network network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
    {
      ConfigurationNetwork = network.ConfigurationNetwork;

      ProtocolStateMachine = new()
      {
        { VersionMessage.Command, new VersionMessage(network.Blockchain) },
        { PingMessage.Command, new PingMessage() },
        { PongMessage.Command, new PongMessage() },
        { UnknownMessage.Command, new UnknownMessage() }
      };

      SocketCommunication = socketCommunication;
      Connection = connection;
    }

    internal bool IsDisposed()
    {
      return StateCurrent == StateProtocol.Disposed;
    }

    internal virtual bool CanDeliverBlock(Header header)
    {
      return !HashesBlockRefused.Contains(header.Hash);
    }

    protected virtual Task RelayTXs()
    {
      return Task.CompletedTask;
    }

    internal async Task Start(int heightBlockchainTip)
    {
      await SocketCommunication.Start(ConfigurationNetwork.MagicBytes);

      StartMessageReceiver();
      StartHeartbeat();

      if (Connection == ConnectionType.OUTBOUND)
        VersionMessage.SendVersion(this, heightBlockchainTip);
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

    async Task StartHeartbeat()
    {
      while (true)
      {
        await Task.Delay(TIMESPAN_HEARTBEAT_MILLISECONDS).ConfigureAwait(false);

        if (IsDisposed())
          return;

        try
        {
          if (StateCurrent != StateProtocol.Handshake)
          {
            await ((PingMessage)ProtocolStateMachine[PingMessage.Command]).Ping(this);

            await RelayTXs();
          }

          if (!SemaphorePeer.Wait(0))
            continue;

          try
          {
            BlockMessage blockMessage = (BlockMessage)ProtocolStateMachine[BlockMessage.Command];

            if (blockMessage.BlockDownload.Header == null && StateCurrent != StateProtocol.DBDownload)
            {
              if (StateCurrent != StateProtocol.Idle)
                continue;

              await blockMessage.RequestBlockNext(this);
            }
            else if (DateTime.UtcNow - blockMessage.TimeRequest > TimeSpan.FromSeconds(TIMEOUT_REQUEST_SECONDS))
              SocketCommunication.Dispose();
          }
          finally
          {
            SemaphorePeer.Release();
          }
        }
        catch
        {
          SocketCommunication.Dispose();
        }
      }
    }
  }
}
