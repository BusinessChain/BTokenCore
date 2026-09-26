using System;


namespace BTokenCore;

internal class Peer
{
  internal Dictionary<string, MessageNetworkProtocol> ProtocolStateMachine;

  internal int Port;
  internal UInt32 ProtocolVersion;
  internal ulong NetworkServicesLocal;
  internal ulong NetworkServicesRemote;
  internal string UserAgent;
  internal byte RelayOption;

  internal ISocketCommunication SocketCommunication;
  internal Network.ConnectionType Connection;

  internal enum StateProtocol
  {
    Handshake,
    AwaitVersion,
    Idle,
    HeaderDownload,
    DBDownload,
    GetData,
    AdvertizingTX,
    Disposed,
    Busy
  }

  internal StateProtocol StateCurrent = StateProtocol.Handshake;

  internal SemaphoreSlim SemaphorePeer = new(1);


  internal Peer(
    Network network,
    ISocketCommunication socketCommunication,
    Network.ConnectionType connection)
  {
    Port = network.Token.Port;
    ProtocolVersion = network.Token.ProtocolVersion;
    NetworkServicesLocal = network.Token.NetworkServicesLocal;
    NetworkServicesRemote = network.Token.NetworkServicesRemote;
    UserAgent = network.Token.UserAgent;
    RelayOption = network.EnableRelay ? (byte)0x01 : (byte)0x00;

    ProtocolStateMachine = network.CreateStateMachineProtocol();
    SocketCommunication = socketCommunication;
    Connection = connection;
  }

  internal bool IsDisposed()
  {
    return StateCurrent == StateProtocol.Disposed;
  }

  internal async Task Start(int heightBlockchainTip)
  {
    await SocketCommunication.Start();

    StartMessageReceiver();

    if (Connection == Network.ConnectionType.OUTBOUND)
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
          MessageNetworkProtocol message = ProtocolStateMachine.GetValueOrDefault(
            commandMessage,
            ProtocolStateMachine[UnknownMessage.Command]);

          await SocketCommunication.LoadMessageNext(message);

          message.DOSMonitor.Increment(1);

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

  async Task SendMessage(MessageNetworkProtocol message)
  {
    await SocketCommunication.SendMessage(message.GetCommand(), message.LengthDataPayload, message.Payload);
  }
}
