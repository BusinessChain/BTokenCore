using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenCore;


public partial class Peer
{
  public Network Network;

  public Dictionary<string, MessageNetworkProtocol> ProtocolStateMachine;

  public ISocketCommunication SocketCommunication;
  public Network.ConnectionType Connection;

  const int TIMEOUT_RESPONSE_MILLISECONDS = 5000;

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

  byte[] HashDBDownload;

  DateTime TimePeerCreation = DateTime.Now;


  public Peer(
    Dictionary<string, MessageNetworkProtocol> protocolStateMachine,
    ISocketCommunication socketCommunication,
    Network.ConnectionType connection)
  {
    ProtocolStateMachine = protocolStateMachine;

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

  public string GetStatus()
  {
    int lifeTime = (int)(DateTime.Now - TimePeerCreation).TotalMinutes;

    lock (this)
      return
        $"\nStatus peer {this}:\n" +
        $"lifeTime minutes: {lifeTime}\n" +
        $"Connection: {Connection}\n";
  }
}
