using System;


namespace BTokenCore;

internal partial class Peer
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
    RelayOption = network.Token.RelayOption;

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
    int numberOfExceptionsUntilDispose = 3;

    while (numberOfExceptionsUntilDispose != 0)
      try
      {
        string commandMessage = await SocketCommunication.ReceiveCommandMessageNext();

        MessageNetworkProtocol message = ProtocolStateMachine[commandMessage];

        await SocketCommunication.LoadMessageNext(message);

        message.DOSMonitor.Increment(1);

        message.Run(this);
      }
      catch
      {
        numberOfExceptionsUntilDispose--;
      }

    SocketCommunication.Dispose();
  }

  async Task SendMessage(MessageNetworkProtocol message)
  {
    await SocketCommunication.SendMessage(message.GetCommand(), message.LengthDataPayload, message.Payload);
  }
}
