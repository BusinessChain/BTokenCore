namespace BTokenCore;

internal partial class Network
{
  protected abstract class Peer
  {
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

      Block blockDownload = new(network.Blockchain.SizeBlockMax);
      Block blockUpload = new(network.Blockchain.SizeBlockMax);

      ProtocolStateMachine = new()
      {
        { GetDataMessage.Command, new GetDataMessage(network.Blockchain, blockUpload) },
        { BlockMessage.Command, new BlockMessage(network.Blockchain, blockDownload) },
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

    internal virtual Task RelayTXs()
    {
      return Task.CompletedTask;
    }

    internal async Task Start(int heightBlockchainTip)
    {
      await SocketCommunication.Start();

      StartMessageReceiver();

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
  }
}
