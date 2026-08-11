using System;


namespace BTokenCore;

public partial class Peer
{
  public Network Network;

  public Dictionary<string, MessageNetworkProtocol> ProtocolStateMachine;

  public ISocketCommunication SocketCommunication;
  public Network.ConnectionType Connection;

  public enum StateProtocol
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

  public StateProtocol StateCurrent = StateProtocol.Handshake;


  public Peer(
    Network network,
    ISocketCommunication socketCommunication,
    Network.ConnectionType connection)
  {
    Network = network;
    ProtocolStateMachine = network.CreateStateMachineProtocol();
    SocketCommunication = socketCommunication;
    Connection = connection;
  }

  public bool IsDisposed()
  {
    return StateCurrent == StateProtocol.Disposed;
  }

  public async Task Start()
  {
    await SocketCommunication.Start();

    StartMessageReceiver();

    if (Connection == Network.ConnectionType.OUTBOUND)
      VersionMessage.SendVersion(this);
  }

  public void BroadcastTX(TX tX)
  {
    InvMessage invMessage = new(new List<Inventory> {
            new(Inventory.InventoryType.MSG_TX, tX.Hash)});

    SendMessage(invMessage);
  }

  public async Task AdvertizeTX(TX tX)
  {
    InvMessage invMessage = new(new List<Inventory> {
          new(Inventory.InventoryType.MSG_TX, tX.Hash)
        });

    await SendMessage(invMessage);
  }

  public string GetIP()
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
