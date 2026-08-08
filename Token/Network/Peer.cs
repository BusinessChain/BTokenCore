using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenCore;

public partial class NetworkToken
{
  partial class Peer
  {
    public NetworkToken Network;

    public Dictionary<string, MessageNetworkProtocol> ProtocolStateMachine;

    ISocketCommunication SocketCommunication;
    public ConnectionType Connection;

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

    Stream NetworkStream;
    CancellationTokenSource Cancellation = new();

    SHA256 SHA256 = SHA256.Create();

    DateTime TimePeerCreation = DateTime.Now;


    public Peer(
      Dictionary<string, MessageNetworkProtocol> protocolStateMachine,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
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
      NetworkStream = await SocketCommunication.Start();

      StartMessageReceiver();

      if (Connection == ConnectionType.OUTBOUND)
        VersionMessage.SendVersion(this);
    }

    public void BroadcastTX(TX tX)
    {
      InvMessage invMessage = new(new List<Inventory> {
            new(InventoryType.MSG_TX, tX.Hash)});

      SendMessage(invMessage);
    }

    public async Task AdvertizeTX(TX tX)
    {
      InvMessage invMessage = new(new List<Inventory> {
          new(InventoryType.MSG_TX, tX.Hash)
        });

      await SendMessage(invMessage);
    }

    public void Dispose()
    {
      Cancellation.Cancel();

      SocketCommunication.Dispose();
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
}
