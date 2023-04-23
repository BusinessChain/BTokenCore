﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;


namespace BTokenLib
{
  public partial class Network
  {
    Token Token;
    Blockchain Blockchain;

    const int TIMEOUT_RESPONSE_MILLISECONDS = 5000;
    const int TIMESPAN_PEER_BANNED_SECONDS = 30;//7 * 24 * 3600;
    const int TIMESPAN_AVERAGE_LOOP_PEER_CONNECTOR_SECONDS = 30;
    const int TIME_LOOP_SYNCHRONIZER_SECONDS = 30;

    StreamWriter LogFile;

    UInt16 Port;

    int CountPeersMax = Math.Max(Environment.ProcessorCount - 1, 4);

    const int COUNT_MAX_INBOUND_CONNECTIONS = 1;
    public bool EnableInboundConnections;

    object LOCK_Peers = new();
    List<Peer> Peers = new();

    public enum ConnectionType { OUTBOUND, INBOUND };

    List<string> PoolIPAddress = new();

    DirectoryInfo DirectoryPeers;
    DirectoryInfo DirectoryPeersActive;
    DirectoryInfo DirectoryPeersDisposed;
    DirectoryInfo DirectoryPeersArchive;

    readonly object LOCK_IsStateSynchronizing = new();
    bool FlagIsSyncingBlocks;
    bool IsStateSynchronizing;
    Peer PeerSynchronizing;
    HeaderDownload HeaderDownload;

    public Network(
      Token token,
      bool flagEnableInboundConnections)
    {
      Token = token;
      Blockchain = token.Blockchain;

      Port = token.Port;
      EnableInboundConnections = flagEnableInboundConnections;

      string pathRoot = token.GetName();

      LogFile = new StreamWriter(
        Path.Combine(pathRoot, "LogNetwork"),
        false);

      DirectoryPeers = Directory.CreateDirectory(
        Path.Combine(pathRoot, "logPeers"));

      DirectoryPeersActive = Directory.CreateDirectory(
        Path.Combine(
          DirectoryPeers.FullName,
          "active"));

      DirectoryPeersDisposed = Directory.CreateDirectory(
        Path.Combine(
          DirectoryPeers.FullName,
          "disposed"));

      DirectoryPeersArchive = Directory.CreateDirectory(
        Path.Combine(DirectoryPeers.FullName, "archive"));

      LoadNetworkConfiguration(pathRoot);

      foreach (FileInfo file in DirectoryPeersActive.GetFiles())
        file.MoveTo(Path.Combine(DirectoryPeersArchive.FullName, file.Name));

      LoadIPAddressPool();
    }

    public void Start()
    {
      $"Start Network {Token.GetName()}".Log(this, LogFile);

      StartPeerConnector();

      StartSynchronizerLoop();

      if (EnableInboundConnections)
        StartPeerInboundListener();
    }

    void LoadNetworkConfiguration(string pathConfigFile)
    {
      $"Load Network configuration {pathConfigFile}.".Log(this, LogFile);
    }

    async Task StartPeerConnector()
    {
      int countPeersCreate;
      Random randomGenerator = new();

      try
      {
        while (true)
        {
          lock (LOCK_Peers)
          {
            Peers.FindAll(p => p.IsStateDiposed()).ForEach(p =>
            {
              Peers.Remove(p);
              p.Dispose();
            });

            countPeersCreate = CountPeersMax - Peers.Count;
          }

          if (countPeersCreate > 0)
          {
            List<string> iPAddresses = new();

            if (PoolIPAddress.Count == 0)
              LoadIPAddressPool();

            while (
              iPAddresses.Count < countPeersCreate &&
              PoolIPAddress.Count > 0)
            {
              int randomIndex = randomGenerator.Next(PoolIPAddress.Count);

              iPAddresses.Add(PoolIPAddress[randomIndex]);
              PoolIPAddress.RemoveAt(randomIndex);
            }

            iPAddresses = iPAddresses.Except(DirectoryPeersActive.EnumerateFiles()
              .Select(f => f.Name)).Except(DirectoryPeersDisposed.EnumerateFiles()
              .Select(f => f.Name)).ToList();

            if (iPAddresses.Count > 0)
            {
              ($"Connect with {iPAddresses.Count} new peers. " +
                $"{Peers.Count} peers connected currently.").Log(this, LogFile);

              var createPeerTasks = new Task[iPAddresses.Count];

              Parallel.For(
                0,
                iPAddresses.Count,
                i => createPeerTasks[i] = CreatePeer(iPAddresses[i]));

              await Task.WhenAll(createPeerTasks);
            }
            else
              ; //$"No ip address found to connect in protocol {Token}.".Log(LogFile);
          }
          int timespanRandomSeconds =
            TIMESPAN_AVERAGE_LOOP_PEER_CONNECTOR_SECONDS / 2 +
            randomGenerator.Next(TIMESPAN_AVERAGE_LOOP_PEER_CONNECTOR_SECONDS);

          await Task.Delay(1000 * timespanRandomSeconds).ConfigureAwait(false);
        }
      }
      catch (Exception ex)
      {
        $"{ex.GetType().Name} in StartPeerConnector of protocol {Token}."
          .Log(this, LogFile);
      }
    }

    void LoadIPAddressPool()
    {
      PoolIPAddress = Token.GetSeedAddresses();

      foreach (FileInfo iPDisposed in DirectoryPeersDisposed.EnumerateFiles())
      {
        TimeSpan timeSpanSinceLastDisposal = DateTime.Now - iPDisposed.LastAccessTime;

        if (0 < TIMESPAN_PEER_BANNED_SECONDS - (int)timeSpanSinceLastDisposal.TotalSeconds)
          continue;

        iPDisposed.MoveTo(Path.Combine(
          DirectoryPeersArchive.FullName,
          iPDisposed.Name));
      }

      foreach (FileInfo fileIPAddress in DirectoryPeersArchive.EnumerateFiles())
        if (!PoolIPAddress.Contains(fileIPAddress.Name))
          PoolIPAddress.Add(fileIPAddress.Name);
    }

    void AddNetworkAddressesAdvertized(
      List<NetworkAddress> addresses)
    {
      foreach (NetworkAddress address in addresses)
      {
        string addressString = address.IPAddress.ToString();

        if (!PoolIPAddress.Contains(addressString))
          PoolIPAddress.Add(addressString);
      }
    }

    async Task StartSynchronizerLoop()
    {
      Peer peer = null;

      while (true)
      {
        await Task.Delay(TIME_LOOP_SYNCHRONIZER_SECONDS * 1000).ConfigureAwait(false);

        lock (LOCK_IsStateSynchronizing)
        {
          if (IsStateSynchronizing)
            continue;

          lock (LOCK_Peers)
          {
            foreach (Peer p in Peers)
              if (p.TrySync() &&
                (peer == null || p.TimeLastSynchronization < peer.TimeLastSynchronization))
              {
                if (peer != null)
                  peer.SetStateIdle();

                peer = p;
              }

            if (peer == null)
              continue;
          }

          if(!Token.TryLock())
          {
            peer.SetStateIdle();
            continue;
          }

          EnterStateSynchronization(peer);
        }

        PeerSynchronizing.SendGetHeaders(HeaderDownload.Locator);
      }
    }

    bool TryEnterStateSynchronization(Peer peer)
    {
      lock (LOCK_IsStateSynchronizing)
      {
        if (IsStateSynchronizing)
        {
          if (PeerSynchronizing == peer)
            $"Remain in state synchronization of with peer {peer}.".Log(this, LogFile);

          return PeerSynchronizing == peer;
        }

        if (!Token.TryLock())
          return false;

        EnterStateSynchronization(peer);

        ($"Peer {peer + peer.Connection.ToString()} of {Token.GetName()} " +
          $"initiated synchronization.").Log(this, LogFile);

        return true;
      }
    }

    void EnterStateSynchronization(Peer peer)
    {
      PeerSynchronizing = peer;
      IsStateSynchronizing = true;
      HeaderDownload = Token.CreateHeaderDownload();
    }

    void ExitSynchronization()
    {
      IsStateSynchronizing = false;
      FlagIsSyncingBlocks = false;
      PeerSynchronizing.SetStateIdle();
      PeerSynchronizing.TimeLastSynchronization = DateTime.Now;
      PeerSynchronizing = null;
      Token.ReleaseLock();
    }

    void HandleExceptionPeerListener(Peer peer)
    {
      lock(LOCK_IsStateSynchronizing)
        if (IsStateSynchronizing && PeerSynchronizing == peer)
          ExitSynchronization();
        else if (peer.IsStateBlockSynchronization())
          ReturnPeerBlockDownloadIncomplete(peer);
        else if (peer.IsStateDBDownload())
          ReturnPeerDBDownloadIncomplete(peer.HashDBDownload);
    }

    void InsertHeader(Header header)
    {
      $"Insert {header} in headerDownload.".Log(LogFile);

      HeaderDownload.InsertHeader(header);
    }

    bool FlagSyncAbort;
    int HeightInsertion;
    object LOCK_HeightInsertion = new();
    const int CAPACITY_MAX_QueueBlocksInsertion = 20;
    Dictionary<int, Block> QueueBlockInsertion = new();
    Dictionary<int, (Header, int)> HeadersBeingDownloadedByCountPeers = new();
    Dictionary<int, Header> QueueDownloadsIncomplete = new();
    Header HeaderRoot;

    bool FlagSyncDBAbort;
    List<byte[]> HashesDB;

    async Task SyncBlocks()
    {
      lock (LOCK_IsStateSynchronizing)
        FlagIsSyncingBlocks = true;

      double difficultyOld = Blockchain.HeaderTip.Difficulty; ;

      try
      {
        if (HeaderDownload.HeaderTip != null)
          if (HeaderDownload.HeaderTip.DifficultyAccumulated >
              Blockchain.HeaderTip.DifficultyAccumulated)
          {
            if (HeaderDownload.HeaderAncestor != Blockchain.HeaderTip)
            {
              $"HeaderDownload.HeaderAncestor {HeaderDownload.HeaderAncestor} not equal to {Blockchain.HeaderTip}".Log(LogFile);
              Token.LoadImage(HeaderDownload.HeaderAncestor.Height);
            }

            FlagSyncAbort = false;
            QueueBlockInsertion.Clear();
            QueueDownloadsIncomplete.Clear();

            HeaderRoot = HeaderDownload.HeaderRoot;
            HeightInsertion = HeaderRoot.Height;

            Peer peer = PeerSynchronizing;

            while (true)
            {
              if (FlagSyncAbort)
              {
                $"Synchronization with {PeerSynchronizing} is aborted.".Log(LogFile);
                Token.LoadImage();

                Peers
                  .Where(p => p.IsStateBlockSynchronization()).ToList()
                  .ForEach(p => p.SetStateIdle());

                while (true)
                {
                  lock (LOCK_Peers)
                    if (!Peers.Any(p => p.IsStateBlockSynchronization()))
                      break;

                  "Waiting for all peers to exit state 'block synchronization'."
                    .Log(LogFile);

                  await Task.Delay(1000).ConfigureAwait(false);
                }

                return;
              }

              if (peer != null)
                if (TryChargeHeader(peer))
                  await peer.RequestBlock();
                else
                {
                  peer.SetStateIdle();

                  if (Peers.All(p => !p.IsStateBlockSynchronization()))
                  {
                    if (
                      difficultyOld > 0 &&
                      Blockchain.HeaderTip.Difficulty > difficultyOld)
                    {
                      Token.Reorganize();
                    }

                    break;
                  }
                }

              TryGetPeer(out peer);

              await Task.Delay(1000).ConfigureAwait(false);
            }
          }
          else
            PeerSynchronizing.SendHeaders(
              new List<Header>() { Blockchain.HeaderTip });

        $"Synchronization with {PeerSynchronizing} of {Token.GetName()} completed."
          .Log(LogFile);
      }
      catch (Exception ex)
      {
        ($"Unexpected exception {ex.GetType().Name} occured during SyncBlocks.\n" +
          $"{ex.Message}").Log(LogFile);
      }
      finally
      {
        Blockchain.GetStatus().Log(LogFile);
        ExitSynchronization();
      }
    }

    bool InsertBlock_FlagContinue(Peer peer)
    {
      Block block = peer.Block;

      lock (LOCK_HeightInsertion)
      {
        if (peer.HeaderSync.Height > HeightInsertion)
        {
          QueueBlockInsertion.Add(
            peer.HeaderSync.Height,
            block);

          if (!PoolBlocks.TryTake(out peer.Block))
            peer.Block = Token.CreateBlock();
        }
        else if (peer.HeaderSync.Height == HeightInsertion)
        {
          bool flagReturnBlockDownloadToPool = false;

          while (true)
          {
            try
            {
              Token.InsertBlock(block);

              block.Clear();

              $"Inserted block {Blockchain.HeaderTip.Height}, {block}."
              .Log(LogFile);
            }
            catch (Exception ex)
            {
              $"Insertion of block {block} failed:\n {ex.Message}.".Log(LogFile);

              FlagSyncAbort = true;

              return false;
            }

            HeightInsertion += 1;

            if (flagReturnBlockDownloadToPool)
              PoolBlocks.Add(block);

            if (!QueueBlockInsertion.TryGetValue(HeightInsertion, out block))
              break;

            QueueBlockInsertion.Remove(HeightInsertion);
            flagReturnBlockDownloadToPool = true;
          }
        }
      }

      return TryChargeHeader(peer);
    }

    object LOCK_ChargeHeader = new();
    ConcurrentBag<Block> PoolBlocks = new();

    bool TryChargeHeader(Peer peer)
    {
      lock (LOCK_ChargeHeader)
      {
        if (
          peer.HeaderSync != null &&
          HeadersBeingDownloadedByCountPeers.ContainsKey(peer.HeaderSync.Height))
        {
          (Header headerBeingDownloaded, int countPeers) =
            HeadersBeingDownloadedByCountPeers[peer.HeaderSync.Height];

          if (countPeers > 1)
            HeadersBeingDownloadedByCountPeers[peer.HeaderSync.Height] =
              (headerBeingDownloaded, countPeers - 1);
          else
            HeadersBeingDownloadedByCountPeers.Remove(peer.HeaderSync.Height);
        }

        if (QueueBlockInsertion.Count > CAPACITY_MAX_QueueBlocksInsertion)
        {
          int keyHeightMin = HeadersBeingDownloadedByCountPeers.Keys.Min();

          (Header headerBeingDownloadedMinHeight, int countPeersMinHeight) =
            HeadersBeingDownloadedByCountPeers[keyHeightMin];

          lock (LOCK_HeightInsertion)
            if (keyHeightMin < HeightInsertion)
              goto LABEL_ChargingWithHeaderMinHeight;

          HeadersBeingDownloadedByCountPeers[keyHeightMin] =
            (headerBeingDownloadedMinHeight, countPeersMinHeight + 1);

          peer.HeaderSync = headerBeingDownloadedMinHeight;
          return true;
        }

      LABEL_ChargingWithHeaderMinHeight:

        while (QueueDownloadsIncomplete.Any())
        {
          int heightSmallestHeadersIncomplete = QueueDownloadsIncomplete.Keys.Min();
          Header header = QueueDownloadsIncomplete[heightSmallestHeadersIncomplete];
          QueueDownloadsIncomplete.Remove(heightSmallestHeadersIncomplete);

          lock (LOCK_HeightInsertion)
            if (heightSmallestHeadersIncomplete < HeightInsertion)
              continue;

          peer.HeaderSync = header;

          return true;
        }

        if (HeaderRoot != null)
        {
          HeadersBeingDownloadedByCountPeers.Add(HeaderRoot.Height, (HeaderRoot, 1));
          peer.HeaderSync = HeaderRoot;
          HeaderRoot = HeaderRoot.HeaderNext;

          return true;
        }

        return false;
      }
    }

    void ReturnPeerBlockDownloadIncomplete(Peer peer)
    {
      lock (LOCK_ChargeHeader)
        if (QueueDownloadsIncomplete.ContainsKey(peer.HeaderSync.Height))
        {
          (Header headerBeingDownloaded, int countPeers) =
            HeadersBeingDownloadedByCountPeers[peer.HeaderSync.Height];

          if (countPeers > 1)
            HeadersBeingDownloadedByCountPeers[peer.HeaderSync.Height] =
              (headerBeingDownloaded, countPeers - 1);
        }
        else
          QueueDownloadsIncomplete.Add(
            peer.HeaderSync.Height,
            peer.HeaderSync);
    }

    async Task SyncDB(Peer peer)
    {
      Peer peerSync = peer;
      HashesDB = peerSync.HashesDB;

      Token.DeleteDB();

      while (true)
      {
        if (FlagSyncDBAbort)
        {
          $"Synchronization with {peerSync} was abort.".Log(LogFile);

          Token.LoadImage();

          lock (LOCK_Peers)
            Peers
              .Where(p => p.IsStateDBDownload()).ToList()
              .ForEach(p => p.SetStateIdle());

          while (true)
          {
            lock (LOCK_Peers)
              if (!Peers.Any(p => p.IsStateDBDownload()))
                break;

            "Waiting for all peers to exit state 'synchronization busy'."
              .Log(LogFile);

            await Task.Delay(1000).ConfigureAwait(false);
          }

          break;
        }

        if (peer != null)
          if (TryChargeHashDB(peer))
            await peer.RequestDB();
          else
          {
            peer.SetStateIdle();

            if (Peers.All(p => !p.IsStateBlockSynchronization()))
              break;
          }

        TryGetPeer(out peer);

        await Task.Delay(1000).ConfigureAwait(false);
      }
    }

    bool InsertDB_FlagContinue(Peer peer)
    {
      try
      {
        Token.InsertDB(peer.Payload, peer.LengthDataPayload);

        $"Inserted DB {peer.HashDBDownload.ToHexString()} from {peer}."
        .Log(LogFile);
      }
      catch (Exception ex)
      {
        ($"Insertion of DB {peer.HashDBDownload.ToHexString()} failed:\n " +
          $"{ex.Message}.").Log(LogFile);

        FlagSyncAbort = true;

        return false;
      }

      return TryChargeHashDB(peer);
    }

    object LOCK_ChargeHashDB = new();
    List<byte[]> QueueHashesDBDownloadIncomplete = new();

    bool TryChargeHashDB(Peer peer)
    {
      lock (LOCK_ChargeHashDB)
      {
        if (QueueHashesDBDownloadIncomplete.Any())
        {
          peer.HashDBDownload = QueueHashesDBDownloadIncomplete[0];
          QueueHashesDBDownloadIncomplete.RemoveAt(0);

          return true;
        }

        if (HashesDB.Any())
        {
          peer.HashDBDownload = HashesDB[0];
          HashesDB.RemoveAt(0);

          return true;
        }

        return false;
      }
    }

    void ReturnPeerDBDownloadIncomplete(byte[] hashDBSync)
    {
      QueueHashesDBDownloadIncomplete.Add(hashDBSync);
    }

    public void AdvertizeBlockToNetwork(Block block)
    {
      AdvertizeBlockToNetwork(block, null);
    }

    void AdvertizeBlockToNetwork(Block block, Peer peerSource)
    {
      Peers.ForEach(p =>
      {
        if (p != peerSource && p.IsStateIdle() &&
        (p.HeaderUnsolicited == null ||
        !p.HeaderUnsolicited.Hash.IsEqual(block.Header.Hash)))
        {
          p.AdvertizeBlock(block);
        }
      });
    }

    bool TryGetPeer(out Peer peer)
    {
      lock (LOCK_Peers)
        peer = Peers.Find(p => p.IsStateIdle());

      return peer != null;
    }

    public void AdvertizeTX(TX tX)
    {
      //$"Advertize rawTX {tX.GetStringTXRaw()} to {this}."
      //  .Log(this, LogFile);

      // should Lock Blockchain

      List<Peer> peersAdvertized = new();

      while (TryGetPeer(out Peer peer))
        peersAdvertized.Add(peer);

      peersAdvertized.Select(p => p.AdvertizeTX(tX))
        .ToArray();
    }

    async Task CreatePeer(string iP)
    {
      Peer peer;

      try
      {
        lock (LOCK_Peers)
        {
          peer = Peers.Find(p => p.IPAddress.Equals(iP));

          if (peer != null)
          {
            $"Connection with peer {peer} already established.".Log(LogFile);
            return;
          }

          peer = new Peer(
            this, 
            Token, 
            IPAddress.Parse(iP), 
            ConnectionType.OUTBOUND);

          Peers.Add(peer);
        }
      }
      catch (Exception ex)
      {
        $"{ex.GetType().Name} when creating peer {iP}:\n{ex.Message}."
        .Log(this, LogFile);

        return;
      }

      try
      {
        $"Connect with peer {peer + peer.Connection.ToString()}."
          .Log(LogFile);

        TcpClient tcpClient = new();

        await tcpClient.ConnectAsync(IPAddress.Parse(iP), Port)
          .ConfigureAwait(false);

        await peer.Connect(tcpClient);

        if (TryEnterStateSynchronization(peer))
          await peer.SendGetHeaders(HeaderDownload.Locator);
      }
      catch (Exception ex)
      {
        $"Could not connect to {peer + peer.Connection.ToString()}: {ex.Message}"
          .Log(LogFile);

        peer.Dispose();
      }
    }

    public void IncrementCountPeersMax()
    {
      CountPeersMax++;
    }

    public void RemovePeer(string iPAddress)
    {
      lock (LOCK_Peers)
      {
        Peer peerRemove = Peers.
          Find(p => p.ToString() == iPAddress);

        if (peerRemove != null)
        {
          CountPeersMax--;
          peerRemove.SetStateDisposed("Manually removed peer.");
        }
      }
    }

    async Task StartPeerInboundListener()
    {
      TcpListener tcpListener = new(IPAddress.Any, Port);
      tcpListener.Start(COUNT_MAX_INBOUND_CONNECTIONS);

      $"Start TCP listener on port {Port}.".Log(this, LogFile);

      while (true)
      {
        TcpClient tcpClient = await tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);

        IPAddress remoteIP =
          ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address;

        $"Received inbound request on port {Port} from {remoteIP}.".Log(this, LogFile);

        Peer peer = null;

        lock (LOCK_Peers)
        {
          string rejectionString = "";

          if (Peers.Count(p => p.Connection == ConnectionType.INBOUND) >= COUNT_MAX_INBOUND_CONNECTIONS)
            rejectionString = $"Max number ({COUNT_MAX_INBOUND_CONNECTIONS}) of inbound connections reached.";

          if (Peers.Any(p => p.IPAddress.Equals(remoteIP)))
            rejectionString = $"Connection already established.";

          if (rejectionString != "")
          {
            $"Reject inbound request from {remoteIP}.\n {rejectionString}"
              .Log(this, LogFile);

            tcpClient.Dispose();
            continue;
          }

          try
          {
            peer = new(
              this,
              Token,
              remoteIP,
              ConnectionType.INBOUND);

            peer.Connect(tcpClient);
          }
          catch (Exception ex)
          {
            ($"Failed to connect to inbound peer {remoteIP}: " +
              $"\n{ex.GetType().Name}: {ex.Message}")
              .Log(this, LogFile);

            peer.Dispose();
            continue;
          }

          Peers.Add(peer);

          $"Accept inbound request from {remoteIP}."
            .Log(this, LogFile);
        }
      }
    }

    public string GetStatus()
    {
      string statusPeers = "";
      int countPeers;

      lock (LOCK_Peers)
      {
        Peers.ForEach(p => { statusPeers += p.GetStatus(); });
        countPeers = Peers.Count;
      }

      return
        "\n\n Status Network: \n" +
        statusPeers +
        $"\n\n Count peers: {countPeers} \n";
    }

    public override string ToString()
    {
      return Token.GetType().Name + "." + GetType().Name;
    }
  }
}
