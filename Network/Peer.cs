using System;


namespace BTokenCore;

internal partial class Peer
{
  internal Network Network;

  internal Dictionary<string, MessageNetworkProtocol> ProtocolStateMachine;

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
    Network = network;
    ProtocolStateMachine = network.CreateStateMachineProtocol();
    SocketCommunication = socketCommunication;
    Connection = connection;
  }

  internal bool IsDisposed()
  {
    return StateCurrent == StateProtocol.Disposed;
  }

  internal async Task Start()
  {
    await SocketCommunication.Start();

    StartMessageReceiver();

    if (Connection == Network.ConnectionType.OUTBOUND)
      VersionMessage.SendVersion(this);
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

        MessageNetworkProtocol message = ProtocolStateMachine[commandMessage];

        await SocketCommunication.LoadMessageNext(message);

        message.DOSMonitor.Increment(1);

        message.Run(this);
      }
    }
    finally
    {
      SocketCommunication.Dispose();
    }
  }

  async Task SendMessage(MessageNetworkProtocol message)
  {
    await SocketCommunication.SendMessage(message.GetCommand(), message.LengthDataPayload, message.Payload);
  }
}
