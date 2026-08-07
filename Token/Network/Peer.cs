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
    public IPAddress IPAddress;

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

    NetworkStream NetworkStream;
    CancellationTokenSource Cancellation = new();

    SHA256 SHA256 = SHA256.Create();

    StreamWriter LogFile;

    DateTime TimePeerCreation = DateTime.Now;

    public Peer(
      Dictionary<string, MessageNetworkProtocol> protocolStateMachine,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
    {
      ProtocolStateMachine = protocolStateMachine;

      SocketCommunication = socketCommunication;
      Connection = connection;
      IPAddress = iPAddress;
    }

    public bool IsDisposed()
    {
      return StateCurrent == Peer.StateProtocol.Disposed;
    }

    public async Task Start()
    {
      if (!TcpClient.Connected)
        await TcpClient.ConnectAsync(IPAddress, Network.Port).ConfigureAwait(false);

      NetworkStream = TcpClient.GetStream();

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

      TcpClient.Dispose();

      LogFile.Dispose();

      //string pathLogFile = ((FileStream)LogFile.BaseStream).Name;
      //string nameLogFile = Path.GetFileName(pathLogFile);
      //string pathLogFileDisposed = Path.Combine(
      //  Network.DirectoryPeersDisposed.FullName, nameLogFile);

      //File.Move(pathLogFile, pathLogFileDisposed);
      //File.SetCreationTime(pathLogFileDisposed, DateTime.Now);
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
