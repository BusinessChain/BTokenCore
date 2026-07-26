using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      // Das Netzwerk holt die Konfiguration vom Token ab.
      int Port;
      UInt32 ProtocolVersion = 70015;
      ulong NetworkServicesLocal = 0;
      ulong NetworkServicesRemote = 0;
      string UserAgent = "/BTokenCore:0.0.0/";
      byte RelayOption = 0x01;

      const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 10;
      const int TIMESPAN_PEER_BANNED_SECONDS = 10;

      const int COUNT_MAX_OUTBOUND_CONNECTIONS = 3;
      const int COUNT_MAX_INBOUND_CONNECTIONS = 3;

      public bool FlagEnableOutboundConnections = true;

      public enum ConnectionType { OUTBOUND, INBOUND };
      List<string> IPAddresses = new();

      object LOCK_Peers = new();
      List<Peer> Peers = new();

      public bool EnableInboundConnections;
      public bool EnableRelay;
      public ILogEntryNotifier LogEntryNotifier;

      DirectoryInfo DirectoryPeers;
      DirectoryInfo DirectoryPeersActive;
      DirectoryInfo DirectoryPeersArchive;
      DirectoryInfo DirectoryPeersDisposed;


      async Task StartPeerConnector()
      {
        if (EnableInboundConnections)
          StartPeerInboundConnector();

        Random randomGenerator = new();

        while (true)
        {
          try
          {
            Peers.RemoveAll(p => p.StateCurrent == Peer.StateProtocol.Disposed);

            int countPeersCreate = COUNT_MAX_OUTBOUND_CONNECTIONS - Peers.Count;

            if (countPeersCreate > 0)
            {
              List<IPAddress> iPAddresses = LoadIPAddresses(countPeersCreate, randomGenerator);

              var createPeerTasks = iPAddresses
                .Select(ip => CreatePeer(new TcpClient(), ConnectionType.OUTBOUND, ip))
                .ToArray();
            }

            int timespanRandomSeconds = TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS / 2
              + randomGenerator.Next(TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS);

            await Task.Delay(1000 * timespanRandomSeconds).ConfigureAwait(false);
          }
          catch (Exception ex)
          {
            Log($"{ex.GetType().Name} in peer connector background process:\n {ex.Message}");

            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
          }
        }
      }

      List<IPAddress> LoadIPAddresses(int maxCount, Random randomGenerator)
      {
        List<string> iPAddresses = new();

        if (IPAddresses.Count == 0)
        {
          IPAddresses = Token.GetSeedAddresses();

          foreach (FileInfo iPDisposed in DirectoryPeersDisposed.EnumerateFiles())
          {
            if (iPDisposed.Name.Contains(ConnectionType.OUTBOUND.ToString()))
            {
              int secondsBanned = TIMESPAN_PEER_BANNED_SECONDS -
                (int)(DateTime.Now - iPDisposed.CreationTime).TotalSeconds;

              if (0 < secondsBanned)
              {
                IPAddresses.RemoveAll(iP => iPDisposed.Name.Contains(iP));
                continue;
              }

              iPDisposed.MoveTo(Path.Combine(
                DirectoryPeersArchive.FullName,
                iPDisposed.Name));
            }
          }

          foreach (FileInfo fileIPAddressArchive in DirectoryPeersArchive.EnumerateFiles())
          {
            string iPFromFile = fileIPAddressArchive.Name.GetIPFromFileName();

            if (!IPAddresses.Any(ip => ip == iPFromFile))
              IPAddresses.Add(iPFromFile);
          }

          foreach (FileInfo fileIPAddressActive in DirectoryPeersActive.EnumerateFiles())
            IPAddresses.RemoveAll(iP => fileIPAddressActive.Name.GetIPFromFileName() == iP);
        }

        while (iPAddresses.Count < maxCount && IPAddresses.Count > 0)
        {
          int randomIndex = randomGenerator.Next(IPAddresses.Count);

          string iPAddress = IPAddresses[randomIndex];
          IPAddresses.RemoveAt(randomIndex);

          if (!Peers.Any(p => p.IPAddress.ToString() == iPAddress))
            iPAddresses.Add(iPAddress);
        }

        return iPAddresses.Select(iP => IPAddress.Parse(iP)).ToList();
      }

      async Task StartPeerInboundConnector()
      {
        TcpListener tcpListener = new(IPAddress.Any, Port);

        try
        {
          Log($"Start TCP listener on port {Port}.");
          tcpListener.Start(COUNT_MAX_INBOUND_CONNECTIONS);
        }
        catch (Exception ex)
        {
          Log($"Failed to start TCP listener on port {Port}.\n {ex.Message}");
          return;
        }

        while (true)
        {
          try
          {
            TcpClient tcpClient = await tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);

            IPAddress remoteIP = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address;

            Log($"Received inbound request on port {Port} from {remoteIP}.");

            if (!ValidateInboundPeer(remoteIP))
            {
              tcpClient.Dispose();
              continue;
            }

            CreatePeer(tcpClient, ConnectionType.INBOUND, remoteIP);
          }
          catch (Exception ex)
          {
            Log($"{ex.GetType().Name} in peer connector background process:\n {ex.Message}");

            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
          }
        }
      }

      bool ValidateInboundPeer(IPAddress remoteIP)
      {
        string rejectionString = "";

        lock (LOCK_Peers)
        {
          if (Peers.Any(p => p.IPAddress.Equals(remoteIP)))
            rejectionString = $"Peer {remoteIP} already connected.";
          else if (Peers.Count(p => p.Connection == ConnectionType.INBOUND) >= COUNT_MAX_INBOUND_CONNECTIONS)
            rejectionString = $"Max number ({COUNT_MAX_INBOUND_CONNECTIONS}) of inbound connections reached.";
        }

        if (rejectionString == "")
        {
          if (remoteIP.ToString() != "84.74.69.100")
            rejectionString = $"Peer {remoteIP} not on whitelist.";
          else
            foreach (FileInfo iPDisposed in DirectoryPeersDisposed.EnumerateFiles())
              if (iPDisposed.Name.Contains(remoteIP.ToString()) && iPDisposed.Name.Contains(ConnectionType.INBOUND.ToString()))
              {
                int secondsBanned = TIMESPAN_PEER_BANNED_SECONDS -
                  (int)(DateTime.Now - iPDisposed.CreationTime).TotalSeconds;

                if (secondsBanned > 0)
                {
                  rejectionString = $"{iPDisposed.Name} is banned for {secondsBanned} seconds.";
                  break;
                }
              }
        }

        if (rejectionString != "")
        {
          Log($"Inbound peer {remoteIP} rejected: \n{rejectionString}");
          return false;
        }

        return true;
      }

      async Task CreatePeer(TcpClient tcpClient, ConnectionType connection, IPAddress iP)
      {
        try
        {
          Peer peer = new(CreateStateMachineProtocol(), tcpClient, connection, iP);

          await peer.Start();

          lock (LOCK_Peers)
            Peers.Add(peer);
        }
        catch (Exception ex)
        {
          Log($"Could not start peer {iP}: {ex.Message}");
          tcpClient.Dispose();
        }
      }

      Dictionary<string, MessageNetworkProtocol> CreateStateMachineProtocol()
      {
        Dictionary<string, MessageNetworkProtocol> protocol = new();

        Block blockDownload = new(Token);
        Block blockUpload = new(Token);

        AddMessageNetworkProtocol(protocol, new GetDataMessage(blockUpload));
        AddMessageNetworkProtocol(protocol, new GetHeadersMessage());
        AddMessageNetworkProtocol(protocol, new HeadersMessage(blockDownload));
        AddMessageNetworkProtocol(protocol, new BlockMessage(blockDownload));
        AddMessageNetworkProtocol(protocol, new TXMessage());
        AddMessageNetworkProtocol(protocol, new VerAckMessage());
        AddMessageNetworkProtocol(protocol, new VersionMessage());

        return protocol;
      }

      static void AddMessageNetworkProtocol(
        Dictionary<string, MessageNetworkProtocol> protocol,
        MessageNetworkProtocol message)
      {
        protocol.Add(message.GetCommand(), message);
      }
    }
  }
}

