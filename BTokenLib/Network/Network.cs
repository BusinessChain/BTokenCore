using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Sockets;


namespace BTokenLib
{
  public partial class Network
  {
    Token Token;
    Blockchain Blockchain;

    const int TIMEOUT_RESPONSE_MILLISECONDS = 3000;
    const int TIMESPAN_PEER_BANNED_SECONDS = 30;
    const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 5;

    public bool EnableInboundConnections;

    StreamWriter LogFile;

    UInt16 Port;

    int CountPeersMax = 3; // Math.Max(Environment.ProcessorCount - 1, 4);

    List<string> IPAddressPool = new();

    object LOCK_Peers = new();
    List<Peer> Peers = new();
    Peer PeerSync;

    List<Block> BlocksCached = new();

    DirectoryInfo DirectoryLogPeers;
    DirectoryInfo DirectoryLogPeersDisposed;

    public Network(Token token, bool flagEnableInboundConnections)
    {
      Token = token;
      Blockchain = token.Blockchain;

      Port = token.Port;
      EnableInboundConnections = flagEnableInboundConnections;

      string pathRoot = token.GetName();

      LogFile = new StreamWriter(
        Path.Combine(pathRoot, "LogNetwork"),
        false);

      DirectoryLogPeers = Directory.CreateDirectory(
        Path.Combine(pathRoot, "logPeers"));

      DirectoryLogPeersDisposed = Directory.CreateDirectory(
        Path.Combine(
          DirectoryLogPeers.FullName,
          "disposed"));

      LoadNetworkConfiguration(pathRoot);

      IPAddressPool = Token.GetSeedAddresses();
    }

    public void Start()
    {
      $"Start Network {Token.GetName()}".Log(this, LogFile);

      StartPeerConnector();

      StartSync();

      if (EnableInboundConnections)
        StartPeerInboundListener();
    }

    void LoadNetworkConfiguration (string pathConfigFile)
    {
      $"Load Network configuration {pathConfigFile}.".Log(this, LogFile);
    }

    async Task StartPeerConnector()
    {
      int countPeersCreate;

      try
      {
        while (true)
        {
          lock (LOCK_Peers)
          {
            Peers.FindAll(p => p.FlagDispose).ForEach(p =>
            {
              Peers.Remove(p);
              p.Dispose();
            });

            countPeersCreate = CountPeersMax - Peers.Count;
          }

          if (countPeersCreate == 0)
            goto LABEL_DelayAndContinue;

          List<string> listExclusion = Peers.Select(
            p => p.IPAddress.ToString()).ToList();

          foreach (FileInfo file in DirectoryLogPeersDisposed.GetFiles())
          {
            Random random = new (file.GetHashCode());
            int randomOffset = random.Next(0, 30);
            int timeSpanBan = TIMESPAN_PEER_BANNED_SECONDS + randomOffset;

            if (DateTime.Now.Subtract(file.LastAccessTime).TotalSeconds >
              timeSpanBan)
              file.Delete();
            else
              listExclusion.Add(file.Name);
          }

          List<string> iPAddresses = RetrieveIPAdresses(
            countPeersCreate,
            listExclusion);

          if (iPAddresses.Count > 0)
          {
            ($"Connect with {countPeersCreate} new peers. " +
              $"{Peers.Count} peers connected currently.").Log(this, LogFile);

            var createPeerTasks = new Task[iPAddresses.Count];

            Parallel.For(
              0,
              iPAddresses.Count,
              i => createPeerTasks[i] = CreatePeer(iPAddresses[i]));

            await Task.WhenAll(createPeerTasks);
          }
          else
            ;//$"No ip address found to connect in protocol {Token}.".Log(LogFile);

          LABEL_DelayAndContinue:

          await Task.Delay(1000 * TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS)
            .ConfigureAwait(false);
        }
      }
      catch (Exception ex)
      {
        $"{ex.GetType().Name} in StartPeerConnector of protocol {Token}."
          .Log(this, LogFile);
      }
    }

    public List<string> RetrieveIPAdresses(
      int countMax,
      List<string> iPAddressesExclusion)
    {
      List<string> iPAddresses = new();
      List<string> iPAddressesTemporaryRemovedFromPool = new();

      iPAddressesExclusion.ForEach(i => {
        if (IPAddressPool.Contains(i))
        {
          IPAddressPool.Remove(i);
          iPAddressesTemporaryRemovedFromPool.Add(i);
        }
      });

      Random randomGenerator = new();

      while (
        iPAddresses.Count < countMax &&
        IPAddressPool.Count > 0)
      {
        int randomIndex = randomGenerator
          .Next(IPAddressPool.Count);

        string iPAddress = IPAddressPool[randomIndex];

        IPAddressPool.RemoveAt(randomIndex);
        iPAddressesTemporaryRemovedFromPool.Add(iPAddress);

        if (!iPAddressesExclusion.Contains(iPAddress))
          iPAddresses.Add(iPAddress);
      }

      IPAddressPool.AddRange(iPAddressesTemporaryRemovedFromPool);

      return iPAddresses;
    }

    async Task CreatePeer(string iP)
    {
      Peer peer;

      lock (LOCK_Peers)
      {
        if (Peers.Any(p => p.IPAddress.Equals(iP)))
          return;

        try
        {
          peer = new Peer(
            this,
            Token,
            IPAddress.Parse(iP));
        }
        catch (Exception ex)
        {
          $"{ex.GetType().Name} when creating peer {iP}:\n{ex.Message}."
          .Log(this, LogFile);

          return;
        }

        Peers.Add(peer);
      }

      try
      {
        await peer.Connect();

        $"Successfully connected with peer {peer + peer.Connection.ToString()}."
          .Log(this, LogFile);
      }
      catch (Exception ex)
      {
        $"Could not connect to {peer + peer.Connection.ToString()}: {ex.Message}"
          .Log(this, LogFile);
        
        peer.Dispose(flagBanPeer: false);

        lock (LOCK_Peers)
          Peers.Remove(peer);
      }

      peer.IsBusy = false;
    }

    public void AddPeer()
    {
      CountPeersMax++;
    }

    public void AddPeer(string iP)
    {
      CreatePeer(iP);
    }

    public void RemovePeer(string iPAddress)
    {
      lock (LOCK_Peers)
      {
        Peer peerRemove = Peers.
          Find(p => p.ToString() == iPAddress);

        if(peerRemove != null)
        {
          CountPeersMax--;
          peerRemove.SetFlagDisposed("Manually removed peer.");
        }
      }
    }

    public void ScheduleSynchronization()
    {
      lock (LOCK_Peers)
        Peers.ForEach(p => p.FlagSyncScheduled = true);
    }

    async Task StartSync()
    {
      Peer peerSync = null;

      while (true)
      {
        await Task.Delay(5000).ConfigureAwait(false);

        lock (LOCK_Peers)
        {
          peerSync = Peers.Find(p => p.TrySync());

          if (peerSync == null)
            continue;
        }

        if (!Token.TryLock())
        {
          peerSync.Release();
          peerSync.FlagSyncScheduled = true;
          continue;
        }

        PeerSync = peerSync;

        $"Start synchronization of {Token.GetName()} with peer {PeerSync + PeerSync.Connection.ToString()}."
          .Log(this, LogFile);

        try
        {
          await PeerSync.GetHeaders();
        }
        catch (Exception ex)
        {
          PeerSync.SetFlagDisposed(
            $"{ex.GetType().Name} in getheaders: \n{ex.Message}");

          Token.ReleaseLock();
        }
      }
    }

    bool FlagSyncAbort;
    int HeightInsertion;
    int HeightInsertionOld;
    object LOCK_HeightInsertion = new();
    const int CAPACITY_MAX_QueueBlocksInsertion = 20;
    Dictionary<int, Block> QueueBlockInsertion = new();
    Dictionary<int, (Header, int)> HeadersBeingDownloadedByCountPeers = new();
    Dictionary<int, Header> QueueDownloadsIncomplete = new();
    Header HeaderRoot;

    async Task Sync()
    {
      double difficultyOld = 0.0;
      HeaderDownload headerDownload = PeerSync.HeaderDownload;

      if (
        headerDownload.HeaderTip != null &&
        headerDownload.HeaderTip.DifficultyAccumulated >
        Blockchain.HeaderTip.DifficultyAccumulated)
      {
        if (headerDownload.HeaderAncestor != Blockchain.HeaderTip)
        {
          difficultyOld = Blockchain.HeaderTip.Difficulty;
          Token.LoadImage(headerDownload.HeaderAncestor.Height);
        }

        FlagSyncAbort = false;
        QueueBlockInsertion.Clear();
        QueueDownloadsIncomplete.Clear();

        HeaderRoot = headerDownload.HeaderRoot;
        HeightInsertion = HeaderRoot.Height;

        Peer peer = PeerSync;

        while (true)
        {
          if (FlagSyncAbort)
          {
            $"Synchronization with {PeerSync} was abort.".Log(LogFile);
            Token.LoadImage();

            lock (LOCK_Peers)
              Peers
                .Where(p => p.IsStateIdle() && p.IsBusy).ToList()
                .ForEach(p => p.Release());

            while (true)
            {
              lock (LOCK_Peers)
                if (!Peers.Any(p => p.IsBusy))
                  break;

              "Waiting for all peers to exit state 'synchronization busy'."
                .Log(LogFile);

              await Task.Delay(1000).ConfigureAwait(false);
            }

            break;
          }

          if (peer != null)
            if (TryChargeHeader(peer))
              await peer.RequestBlock();
            else
            {
              peer.Release();

              if (Peers.All(p => !p.IsStateBlockDownload()))
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
        PeerSync.SendHeaders(
          new List<Header>() { Blockchain.HeaderTip });

      $"Synchronization with {PeerSync} of {Token.GetName()} completed."
        .Log(LogFile);

      if (HeightInsertion == HeightInsertionOld)
      {
        if(Token.TokenParent != null)
          Token.GetParentRoot().Network.ScheduleSynchronization();
      }
      else if (Token.TokenChilds.Any())
        Token.TokenChilds.ForEach(t => t.Network.ScheduleSynchronization());
      else if (Token.TokenParent != null)
        Token.GetParentRoot().Network.ScheduleSynchronization();

      HeightInsertionOld = HeightInsertion;

      PeerSync.Release();
      Token.ReleaseLock();
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

    bool FlagSyncDBAbort;
    List<byte[]> HashesDB;

    async Task SyncDB(List<byte[]> hashesDB)
    {
      Peer peer = PeerSync;
      HashesDB = hashesDB;

      while (true)
      {
        if (FlagSyncDBAbort)
        {
          $"Synchronization with {PeerSync} was abort.".Log(LogFile);
          Token.LoadImage();

          lock (LOCK_Peers)
            Peers
              .Where(p => p.IsStateIdle() && p.IsBusy).ToList()
              .ForEach(p => p.Release());

          while (true)
          {
            lock (LOCK_Peers)
              if (!Peers.Any(p => p.IsBusy))
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
            peer.Release();

            if (Peers.All(p => !p.IsStateBlockDownload()))
              break;
          }

        TryGetPeer(out peer);

        await Task.Delay(1000).ConfigureAwait(false);
      }

      Sync();
    }

    bool InsertDB_FlagContinue(Peer peer)
    {
      try
      {
        Token.InsertDB(peer);

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
          peer.HashDBSync = QueueHashesDBDownloadIncomplete[0];
          QueueHashesDBDownloadIncomplete.RemoveAt(0);

          return true;
        }

        if (HashesDB.Any())
        {
          peer.HashDBSync = HashesDB[0];
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

    object LOCK_FlagThrottle = new();
    bool FlagThrottle;

    void ThrottleDownloadBlockUnsolicited()
    {
      while(true)
      {
        lock(LOCK_FlagThrottle)
          if (!FlagThrottle)
          {
            FlagThrottle = true;
            break;
          }

        Thread.Sleep(100);
      }

      StartTimerLatchFlagThrottle();
    }

    async Task StartTimerLatchFlagThrottle()
    {
      await Task.Delay(1000).ConfigureAwait(false);

      lock (LOCK_FlagThrottle)
        FlagThrottle = false;
    }

    public void RelayBlockToNetwork(Block block)
    {
      RelayBlockToNetwork(block, null);
    }

    void RelayBlockToNetwork(Block block, Peer peerSource)
    {
      Peers.ForEach(p =>
      {
        if (p != peerSource && p.TryGetBusy() &&
        (p.HeaderUnsolicited == null ||
        !p.HeaderUnsolicited.Hash.IsEqual(block.Header.Hash)))
        {
          p.RelayBlock(block);
        }
      });
    }

    bool TryGetPeer(out Peer peer)
    {
      lock (LOCK_Peers)
        peer = Peers.Find(p => p.TryGetBusy());

      return peer != null;
    }

    public void AdvertizeTX(TX tX)
    {
      $"Advertize rawTX {tX.GetStringTXRaw()} to {this}."
        .Log(this, LogFile);

      // should Lock Blockchain

      List<Peer> peersAdvertized = new();

      while (TryGetPeer(out Peer peer))
        peersAdvertized.Add(peer);

      peersAdvertized.Select(p => p.AdvertizeTX(tX))
        .ToArray();
    }


    const int PEERS_COUNT_INBOUND = 8;
    TcpListener TcpListener;

    async Task StartPeerInboundListener()
    {
      TcpListener = new(IPAddress.Any, Port);
      TcpListener.Start(PEERS_COUNT_INBOUND);

      $"Start TCP listener on port {Port}".Log(this, LogFile);

      while (true)
      {
        TcpClient tcpClient = await TcpListener.AcceptTcpClientAsync().
          ConfigureAwait(false);

        IPAddress remoteIP = 
          ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address;

        $"Received inbound request on port {Port} from {remoteIP}"
          .Log(this, LogFile);

        Peer peer = null;

        lock (LOCK_Peers)
        {
          if (Peers.Any(p => p.IPAddress.Equals(remoteIP)))
          {
            $"There is already a connection to {remoteIP}."
              .Log(this, LogFile);

            tcpClient.Dispose();
            continue;
          }

          try
          {
            peer = new(
              this,
              Token,
              tcpClient);

            peer.StartMessageListener();
          }
          catch (Exception ex)
          {
            ($"Failed to start listening to inbound peer {remoteIP}: " +
              $"\n{ex.GetType().Name}: {ex.Message}")
              .Log(this, LogFile);

            continue;
          }
          
          Peers.Add(peer);

          peer.IsBusy = false;

          $"Accept inbound request from {remoteIP}."
            .Log(this, LogFile);
        }  
      }
    }

    public string GetStatus()
    {
      string statusPeers = "";
      int countPeers;
      
      lock(LOCK_Peers)
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
