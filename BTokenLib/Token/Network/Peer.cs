using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      partial class Peer
      {
        public NetworkToken Network;

        public Dictionary<string, MessageNetworkProtocol> ProtocolStateMachine;

        TcpClient TcpClient;
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

        ILogEntryNotifier LogEntryNotifier;
        // Only the network should create a log file.
        StreamWriter LogFile;

        DateTime TimePeerCreation = DateTime.Now;


        public Peer(
          Dictionary<string, MessageNetworkProtocol> protocolStateMachine,
          TcpClient tcpClient,
          ConnectionType connection,
          IPAddress iPAddress)
        {
          ProtocolStateMachine = protocolStateMachine;

          TcpClient = tcpClient;
          Connection = connection;
          IPAddress = iPAddress;

          //CreateLogFile($"{ip}-{Connection}");
        }

        //void CreateLogFile(string name)
        //{
        //  string pathLogFileActive = Path.Combine(
        //    Network.DirectoryPeersActive.FullName,
        //    name);

        //  if (File.Exists(pathLogFileActive))
        //    throw new ProtocolException($"Peer {this} already active.");

        //  string pathLogFileDisposed = Path.Combine(
        //    Network.DirectoryPeersDisposed.FullName,
        //    name);

        //  if (File.Exists(pathLogFileDisposed))
        //  {
        //    TimeSpan secondsSincePeerDisposal = TimePeerCreation - File.GetLastWriteTime(pathLogFileDisposed);
        //    int secondsBannedRemaining = TIMESPAN_PEER_BANNED_SECONDS - (int)secondsSincePeerDisposal.TotalSeconds;

        //    if (secondsBannedRemaining > 0)
        //      throw new ProtocolException(
        //        $"Peer {this} is banned for {secondsBannedRemaining} seconds.");

        //    File.Move(pathLogFileDisposed, pathLogFileActive);
        //  }

        //  string pathLogFileArchive = Path.Combine(
        //    Network.DirectoryPeersArchive.FullName,
        //    name);

        //  if (File.Exists(pathLogFileArchive))
        //    File.Move(pathLogFileArchive, pathLogFileActive);

        //  LogFile = new StreamWriter(
        //    pathLogFileActive,
        //    append: true);
        //}

        public async Task Start()
        {
          Log($"Start peer - {Connection}.");

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
          Log($"Advertize token {tX}.");

          InvMessage invMessage = new(new List<Inventory> {
          new(InventoryType.MSG_TX, tX.Hash)
        });

          await SendMessage(invMessage);
        }

        public void Dispose()
        {
          Log($"Dispose {Connection}.");

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

        public void Log(string messageLog)
        {
          messageLog.Log(this, LogEntryNotifier);
        }
      }
    }
  }
}
