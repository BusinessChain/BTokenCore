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

    StreamWriter LogFile;

    const UInt16 Port = 8333; // Load from correct conf File

    int CountPeersMax = 6; // Math.Max(Environment.ProcessorCount - 1, 4);

    List<string> IPAddressPool = new();

    object LOCK_Peers = new();
    List<Peer> Peers = new();
    Peer PeerSync;

    List<Block> BlocksCached = new();

    DirectoryInfo DirectoryLogPeers;
    DirectoryInfo DirectoryLogPeersDisposed;

    public Network(Token token)
    {
      Token = token;
      Blockchain = token.Blockchain;

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
      $"Start Network {Token.GetName()}".Log(LogFile);

      StartPeerConnector(); 

      StartSync();

      StartPeerInboundListener();
    }

    void LoadNetworkConfiguration (string pathConfigFile)
    {
      $"Load Network configuration {pathConfigFile}.".Log(LogFile);
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

          List<string> listExclusion = Peers.Select(p => p.ToString()).ToList();

          foreach (FileInfo file in DirectoryLogPeersDisposed.GetFiles())
            if (DateTime.Now.Subtract(file.LastAccessTime).TotalHours > 4)
              file.Delete();
            else
              listExclusion.Add(file.Name);

          List<string> iPAddresses = RetrieveIPAdresses(
            countPeersCreate,
            listExclusion);

          if (iPAddresses.Count > 0)
          {
            ($"Connect with {countPeersCreate} new peers. " +
              $"{Peers.Count} peers connected currently.").Log(LogFile);

            var createPeerTasks = new Task[iPAddresses.Count];

            Parallel.For(
              0,
              iPAddresses.Count,
              i => createPeerTasks[i] = CreatePeer(iPAddresses[i]));

            await Task.WhenAll(createPeerTasks);
          }
          else
            ;// $"No ip address found to connect in protocol {Token}.".Log(LogFile);

          LABEL_DelayAndContinue:

          await Task.Delay(5000).ConfigureAwait(false);
        }
      }
      catch (Exception ex)
      {
        $"{ex.GetType().Name} in StartPeerConnector of protocol {Token}."
          .Log(LogFile);
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
      lock (LOCK_Peers)
        if (Peers.Any(p => p.IPAddress.Equals(iP)))
          return;

      Peer peer;

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
        .Log(LogFile);

        return;
      }

      try
      {
        await peer.Connect();
      }
      catch (Exception ex)
      {
        peer.SetFlagDisposed(
          $"{ex.GetType().Name} when connecting.: \n{ex.Message}");
      }

      lock (LOCK_Peers)
        Peers.Add(peer);
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

        $"Start synchronization of {Token.GetName()} with peer {PeerSync}."
          .Log(LogFile);

        try
        {
          await PeerSync.GetHeaders(Token.CreateHeaderDownload());
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
            else if (Peers.Any(p => p.IsStateBlockDownload()))
              peer.Release();
            else
            {
              if (
                difficultyOld > 0 &&
                Blockchain.HeaderTip.Difficulty > difficultyOld)
              {
                Token.Reorganize();
              }

              break;
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

    bool InsertBlockFlagContinue(Peer peer)
    {
      Block block = peer.Block;

      lock (LOCK_HeightInsertion)
      {
        if (peer.Header.Height > HeightInsertion)
        {
          QueueBlockInsertion.Add(
            peer.Header.Height,
            block);

          if (!PoolBlocks.TryTake(out peer.Block))
            peer.Block = Token.CreateBlock();
        }
        else if (peer.Header.Height == HeightInsertion)
        {
          bool flagReturnBlockDownloadToPool = false;

          while (true)
          {
            try
            {
              Token.InsertBlock(block, peer);

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
        if(
          peer.Header != null && 
          HeadersBeingDownloadedByCountPeers.ContainsKey(peer.Header.Height))
        {
          (Header headerBeingDownloaded, int countPeers) =
            HeadersBeingDownloadedByCountPeers[peer.Header.Height];

          if (countPeers > 1)
            HeadersBeingDownloadedByCountPeers[peer.Header.Height] = 
              (headerBeingDownloaded, countPeers - 1);
          else
            HeadersBeingDownloadedByCountPeers.Remove(peer.Header.Height);
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

          peer.Header = headerBeingDownloadedMinHeight;
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

          peer.Header = header;

          return true;
        }

        if (HeaderRoot != null)
        {
          HeadersBeingDownloadedByCountPeers.Add(HeaderRoot.Height, (HeaderRoot, 1));
          peer.Header = HeaderRoot;
          HeaderRoot = HeaderRoot.HeaderNext;

          return true;
        }

        return false;
      }
    }

    void ReturnPeerBlockDownloadIncomplete(Peer peer)
    {
      lock (LOCK_ChargeHeader)
        if (QueueDownloadsIncomplete.ContainsKey(peer.Header.Height))
        {
          (Header headerBeingDownloaded, int countPeers) =
            HeadersBeingDownloadedByCountPeers[peer.Header.Height];

          if (countPeers > 1)
            HeadersBeingDownloadedByCountPeers[peer.Header.Height] = 
              (headerBeingDownloaded, countPeers - 1);
        }
        else
        {
          QueueDownloadsIncomplete.Add(
            peer.Header.Height,
            peer.Header);
        }
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

    public void RelayBlock(Block block)
    {
      RelayBlock(block, null);
    }

    void RelayBlock(Block block, Peer peerSource)
    {
      Peers.ForEach(p =>
      {
        if (p != peerSource &&
        (p.HeaderUnsolicited == null ||
        !p.HeaderUnsolicited.Hash.IsEqual(block.Header.Hash)))
        {
          p.SendHeaders(new List<Header>() { block.Header });
        }
      });
    }

    bool TryGetPeer(out Peer peer)
    {
      lock (LOCK_Peers)
        peer = Peers.Find(p => p.TryGetBusy());

      return peer != null;
    }

    public void AdvertizeTX(byte[] hash)
    {
      List<Peer> peers = new();

      // should Lock Blockchain

      while (true)
        if (TryGetPeer(out Peer peer))
          peers.Add(peer);
        else if (peers.Any())
          break;

      peers.Select(p => p.AdvertizeTX(hash))
        .ToArray();
    }


    const int PEERS_COUNT_INBOUND = 8;
    TcpListener TcpListener = new(IPAddress.Any, Port);

    async Task StartPeerInboundListener()
    {
      TcpListener.Start(PEERS_COUNT_INBOUND);

      while (true)
      {
        TcpClient tcpClient = await TcpListener.AcceptTcpClientAsync().
          ConfigureAwait(false);

        IPAddress remoteIP = 
          ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address;

        lock (LOCK_Peers)
          if (Peers.Any(p => p.IPAddress.Equals(remoteIP)))
            continue;

        $"Accept inbound request from {remoteIP}.".Log(LogFile);
        
        Peer peer = null;

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
            .Log(LogFile);

          continue;
        }

        lock (LOCK_Peers)
          Peers.Add(peer);
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
        "\n\n Status Network:\n" +
        statusPeers +
        $"\n\nCount peers: {countPeers}\n";
    }
  }
}
