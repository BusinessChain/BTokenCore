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
  partial class Network
  {
    Token Token;
    Blockchain Blockchain;

    const int UTXOIMAGE_INTERVAL_SYNC = 200;
    const int TIMEOUT_RESPONSE_MILLISECONDS = 20000;

    StreamWriter LogFile;

    const UInt16 Port = 8333;

    int CountPeersMax = 0; // Math.Max(Environment.ProcessorCount - 1, 4);

    object LOCK_Peers = new();
    List<Peer> Peers = new();
    ConcurrentBag<BlockDownload> PoolBlockDownload = new();

    static DirectoryInfo DirectoryLogPeers;
    static DirectoryInfo DirectoryLogPeersDisposed;



    public Network(Token token, Blockchain blockchain)
    {
      Token = token;
      Blockchain = blockchain;

      string pathRoot = token.GetName();

      LogFile = new StreamWriter(
        Path.Combine(pathRoot + "LogNetwork"),
        false);

      DirectoryLogPeers = Directory.CreateDirectory(
        "logPeers");

      DirectoryLogPeersDisposed = Directory.CreateDirectory(
        Path.Combine(
          DirectoryLogPeers.FullName,
          "disposed"));

      LoadNetworkConfiguration(pathRoot);
    }


    public void Start()
    {
      StartPeerConnector();

      StartSynchronizer();

      //"Start listener for inbound connection requests."
      //  .Log(LogFile);

    }


    void LoadNetworkConfiguration (string pathConfigFile)
    {
      "Load Network configuration.".Log(LogFile);
    }

    async Task StartPeerConnector()
    {
      int countPeersCreate;

      while (true)
      {
        lock (LOCK_Peers)
        {
          List<Peer> peersDispose = Peers.FindAll(
            p => p.FlagDispose);

          peersDispose.ForEach(p =>
          {
            Peers.Remove(p);
            p.Dispose();
          });

          countPeersCreate = CountPeersMax - Peers.Count;
        }

        if (countPeersCreate > 0)
        {
          ($"Connect with {countPeersCreate} new peers. " +
            $"{Peers.Count} peers connected currently.")
            .Log(LogFile);

          List<IPAddress> iPAddresses =
            RetrieveIPAddresses(countPeersCreate);

          if (iPAddresses.Count > 0)
          {
            var createPeerTasks = new Task[iPAddresses.Count];

            Parallel.For(
              0,
              iPAddresses.Count,
              i => createPeerTasks[i] = CreatePeer(iPAddresses[i]));

            await Task.WhenAll(createPeerTasks);
          }
          else
          {
            "No peer found to connect.".Log(LogFile);
          }
        }

        await Task.Delay(10000).ConfigureAwait(false);
      }
    }

    List<IPAddress> RetrieveIPAddresses(int countMax)
    {
      List<IPAddress> iPAddresses = new();

      while (iPAddresses.Count < countMax)
      {
        if (AddressPool.Count == 0)
        {
          DownloadIPAddressesFromSeeds();

          lock (LOCK_Peers)
          {
            AddressPool.RemoveAll(
              a => Peers.Any(p => p.ToString() == a.ToString()));
          }

          foreach (FileInfo file in
            DirectoryLogPeersDisposed.GetFiles())
          {
            if (DateTime.Now.Subtract(
              file.LastAccessTime).TotalHours > 24)
            {
              file.Delete();
            }
            else
            {
              AddressPool.RemoveAll(
                a => a.ToString() == file.Name);
            }
          }

          if (AddressPool.Count == 0)
          {
            return iPAddresses;
          }
        }

        int randomIndex = RandomGenerator
          .Next(AddressPool.Count);

        IPAddress iPAddress = AddressPool[randomIndex];
        AddressPool.Remove(iPAddress);

        if (iPAddresses.Any(
          ip => ip.ToString() == iPAddress.ToString()))
        {
          continue;
        }

        iPAddresses.Add(iPAddress);
      }

      return iPAddresses;
    }

    async Task CreatePeer(IPAddress iPAddress)
    {
      Peer peer;

      try
      {
        peer = new Peer(
          this,
          Blockchain,
          Token,
          iPAddress);
      }
      catch (Exception ex)
      {

        ($"{ex.GetType()} when creating peer {iPAddress}: " +
        $"\n{ex.Message}")
        .Log(LogFile);

        return;
      }

      try
      {
        await peer.Connect(Port);
      }
      catch (Exception ex)
      {
        peer.SetFlagDisposed(
          $"{ex.GetType().Name} when connecting.: \n{ex.Message}");
      }

      lock (LOCK_Peers)
      {
        Peers.Add(peer);
      }
    }

    public void AddPeer()
    {
      CountPeersMax++;
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


    List<IPAddress> AddressPool = new();
    Random RandomGenerator = new();

    void DownloadIPAddressesFromSeeds()
    {
      string pathFileSeeds = "DNSSeeds";
      string[] dnsSeeds;

      while (true)
      {
        try
        {
          dnsSeeds = File.ReadAllLines(pathFileSeeds);

          break;
        }
        catch (Exception ex)
        {
          Console.WriteLine(
            "{0} when reading file with DNS seeds {1} \n" +
            "{2} \n" +
            "Try again in 10 seconds ...",
            ex.GetType().Name,
            pathFileSeeds,
            ex.Message);

          Thread.Sleep(10000);
        }
      }

      foreach (string dnsSeed in dnsSeeds)
      {
        if (dnsSeed.Substring(0, 2) == "//")
        {
          continue;
        }

        List<IPAddress> dnsSeedAddresses;

        try
        {
          dnsSeedAddresses =
            Dns.GetHostEntry(dnsSeed).AddressList.ToList();
        }
        catch (Exception ex)
        {
          // If error persists, remove seed from file.

          string.Format(
            "Cannot get peer address from dns server {0}: \n{1}",
            dnsSeed,
            ex.Message)
            .Log(LogFile);

          continue;
        }

        dnsSeedAddresses.RemoveAll(ip => 
        ip.AddressFamily == AddressFamily.InterNetworkV6);

        AddressPool = AddressPool.Union(
          dnsSeedAddresses.Distinct())
          .ToList();
      }
    }



    Peer PeerSynchronization;

    async Task StartSynchronizer()
    {
      while (true)
      {
        await Task.Delay(5000).ConfigureAwait(false);

        if (!Blockchain.TryLock())
        {
          continue;
        }

        lock (LOCK_Peers)
        {
          PeerSynchronization = Peers.Find(p => p.TryStageSynchronization());

          if (PeerSynchronization == null)
          {
            Blockchain.ReleaseLock();
            continue;
          }
        }

        $"Start synchronization with peer {PeerSynchronization}."
          .Log(LogFile);

        try
        {
          await PeerSynchronization.GetHeaders();
        }
        catch (Exception ex)
        {
          PeerSynchronization.SetFlagDisposed(
            $"{ex.GetType().Name} in getheaders: \n{ex.Message}");

          Blockchain.ReleaseLock();
          continue;
        }
      }
    }

    bool FlagSynchronizationAbort;
    object LOCK_InsertBlockDownload = new();
    int IndexInsertion;
    Dictionary<int, BlockDownload> QueueDownloadsInsertion = new();
    List<BlockDownload> QueueDownloadsIncomplete = new();
    Header HeaderRoot;
    int IndexBlockDownload;

    void Synchronize()
    {
      HeaderDownload headerDownload = PeerSynchronization.HeaderDownload;

      if (headerDownload.HeaderLocatorAncestor != Blockchain.HeaderTip)
      {
        if (
          headerDownload.HeaderTip == null ||
          headerDownload.HeaderTip.DifficultyAccumulated <=
          Blockchain.HeaderTip.DifficultyAccumulated ||
          !Blockchain.TryFork(headerDownload.HeaderLocatorAncestor))
        {
          PeerSynchronization.Release();
          Blockchain.ReleaseLock();
          return;
        }
      }

      FlagSynchronizationAbort = false;
      IndexInsertion = 0;
      QueueDownloadsInsertion.Clear();
      QueueDownloadsIncomplete.Clear();

      IndexBlockDownload = 0;
      HeaderRoot = headerDownload.HeaderRoot;

      SynchronizeBlocks();
    }

    async Task SynchronizeBlocks()
    {
      try
      {
        Peer peer = PeerSynchronization;

        while (!FlagSynchronizationAbort)
        {
          if (peer != null)
          {
            $"Sync with peer {peer}".Log(LogFile);

            if (!TryChargeBlockDownload(peer))
              break;

            await peer.GetBlock();
          }

          if (!TryGetPeer(out peer))
          {
            await Task.Delay(3000).ConfigureAwait(false);
          }
        }

        if (Blockchain.IsFork)
        {
          if (Blockchain.HeaderTip.Difficulty > Blockchain.DifficultyOld)
          {
            Blockchain.Reorganize();
          }
          else
          {
            Blockchain.DismissFork();
            FlagSynchronizationAbort = true;
          }
        }

        if(FlagSynchronizationAbort)
        {
          string.Format("Synchronization abort. Reload Image").Log(LogFile);
          Blockchain.LoadImage();
        }

        lock (LOCK_Peers)
        {
          Peers
            .Where(p => p.IsStateIdle() && p.IsBusy).ToList()
            .ForEach(p => p.Release());
        }

        while (true)
        {
          lock (LOCK_Peers)
          {
            if (!Peers.Any(p => p.IsBusy))
            {
              break;
            }
          }

          "Waiting for all peers to exit state 'synchronization busy'."
            .Log(LogFile);

          await Task.Delay(1000).ConfigureAwait(false);
        }
      }
      catch(Exception ex)
      {
        $"{ex.GetType().Name} when synchronizing blocks:\n {ex.Message}"
          .Log(LogFile);
      }
      finally
      {
        PoolBlockDownload.Clear();

        Blockchain.ReleaseLock();

        "Synchronization ended.".Log(LogFile);
      }
    }

    BlockDownload BlockDownloadBlocking;
    BlockDownload BlockDownloadIndexPrevious;

    bool InsertBlockDownloadFlagContinue(Peer peer)
    {
      BlockDownload blockDownload = peer.BlockDownload;

      lock (LOCK_InsertBlockDownload)
      {
        if (blockDownload.Index > IndexInsertion)
        {
          QueueDownloadsInsertion.Add(
            blockDownload.Index,
            blockDownload);

          if (QueueDownloadsInsertion.Count > 3 * CountPeersMax)
          {
            if (BlockDownloadBlocking == null)
            {
              BlockDownloadBlocking = blockDownload.BlockDownloadIndexPrevious;
              while (BlockDownloadBlocking.Index > IndexInsertion)
              {
                BlockDownloadBlocking = BlockDownloadBlocking.BlockDownloadIndexPrevious;
              }

              BlockDownloadBlocking.Peer.CountBlockingBlockDownload++;
            }

            if (!PoolBlockDownload.TryTake(out blockDownload))
            {
              blockDownload = new BlockDownload(Token);
            }

            blockDownload.Peer = peer;
            blockDownload.Index = BlockDownloadBlocking.Index;
            blockDownload.HeadersExpected = BlockDownloadBlocking.HeadersExpected.ToList();
            blockDownload.IndexHeadersExpected = 0;
            blockDownload.BlockDownloadIndexPrevious = BlockDownloadBlocking.BlockDownloadIndexPrevious;

            peer.BlockDownload = blockDownload;
            return true;
          }
          else
          {
            peer.BlockDownload = null;
          }
        }
        else if (blockDownload.Index == IndexInsertion)
        {
          bool flagReturnBlockDownloadToPool = false;

          while (true)
          {
            blockDownload.Peer.CountInsertBlockDownload++;

            try
            {
              for (int i = 0; i < blockDownload.HeadersExpected.Count; i += 1)
              {
                Blockchain.InsertBlock(
                    blockDownload.Blocks[i],
                    intervalArchiveImage: UTXOIMAGE_INTERVAL_SYNC);
              }

            ($"Inserted blockDownload {blockDownload.Index} from peer {blockDownload.Peer}. " +
              $"Height: {Blockchain.HeaderTip.Height}, " +
              $"DownloadTime [ms]: {blockDownload.StopwatchBlockDownload.ElapsedMilliseconds}")
              .Log(LogFile);
            }
            catch (Exception ex)
            {
              blockDownload.Peer.SetFlagDisposed(
                $"Insertion of block download {blockDownload.Index} failed:\n" +
                $"{ex.Message}.");

              FlagSynchronizationAbort = true;

              return false;
            }

            if (
              BlockDownloadBlocking != null &&
              BlockDownloadBlocking.Index == blockDownload.Index)
            {
              BlockDownloadBlocking = null;
            }

            IndexInsertion += 1;

            if (flagReturnBlockDownloadToPool)
            {
              PoolBlockDownload.Add(blockDownload);
            }

            if (!QueueDownloadsInsertion.TryGetValue(
              IndexInsertion,
              out blockDownload))
            {
              break;
            }

            QueueDownloadsInsertion.Remove(IndexInsertion);
            flagReturnBlockDownloadToPool = true;
          }
        }
        else
        {
          peer.CountWastedBlockDownload++;
        }
      }

      return TryChargeBlockDownload(peer);
    }

    object LOCK_ChargeFromHeaderRoot = new();

    bool TryChargeBlockDownload(Peer peer)
    {
      BlockDownload blockDownload = peer.BlockDownload;

      lock(QueueDownloadsIncomplete)
      {
        if (QueueDownloadsIncomplete.Any())
        {
          if (blockDownload != null)
          {
            PoolBlockDownload.Add(blockDownload);
          }

          blockDownload = QueueDownloadsIncomplete.First();
          QueueDownloadsIncomplete.RemoveAt(0);

          blockDownload.Peer = peer;

          peer.BlockDownload = blockDownload;

          return true;
        }
      }
      
      lock(LOCK_ChargeFromHeaderRoot)
      {
        if (HeaderRoot != null)
        {
          if (blockDownload == null &&
            !PoolBlockDownload.TryTake(out blockDownload))
          {
            blockDownload = new(Token);
          }

          blockDownload.Peer = peer;
          blockDownload.Index = IndexBlockDownload;
          blockDownload.LoadHeaders(ref HeaderRoot);
          blockDownload.BlockDownloadIndexPrevious = BlockDownloadIndexPrevious;

          BlockDownloadIndexPrevious = blockDownload;

          IndexBlockDownload += 1;

          peer.BlockDownload = blockDownload;

          return true;
        }
      }

      return false;
    }

    void ReturnPeerBlockDownloadIncomplete(Peer peer)
    {
      lock (LOCK_InsertBlockDownload)
      {
        if (BlockDownloadBlocking == peer.BlockDownload)
        {
          return;
        }
      }

      BlockDownload blockDownload = peer.BlockDownload;

      peer.BlockDownload = null;

      blockDownload.Peer = null;
      blockDownload.IndexHeadersExpected = 0;
      blockDownload.CountBytes = 0;

      lock(QueueDownloadsIncomplete)
      {
        int indexInsertInQueue = QueueDownloadsIncomplete
          .FindIndex(b => blockDownload.Index < b.Index);

        if (indexInsertInQueue == -1)
        {
          QueueDownloadsIncomplete.Add(blockDownload);
        }
        else
        {
          QueueDownloadsIncomplete.Insert(
            indexInsertInQueue,
            blockDownload);
        }
      }
    }

    object LOCK_FlagThrottle = new();
    bool FlagThrottle;

    void ThrottleDownloadBlockUnsolicited()
    {
      while(true)
      {
        lock(LOCK_FlagThrottle)
        {
          if (!FlagThrottle)
          {
            FlagThrottle = true;
            break;
          }
        }

        Thread.Sleep(20);
      }

      StartTimerLatchFlagThrottle();
    }

    async Task StartTimerLatchFlagThrottle()
    {
      await Task.Delay(300).ConfigureAwait(false);

      lock (LOCK_FlagThrottle)
        FlagThrottle = false;
    }

    List<Header> QueueBlocksUnsolicited = new();

    async Task<bool> EnqueuBlockUnsolicitedFlagReject(
      Header header)
    {
      lock (QueueBlocksUnsolicited)
      {
        QueueBlocksUnsolicited.Add(header);
      }

      int countTriesLeft = 10;

      while (true)
      {
        lock (QueueBlocksUnsolicited)
        {
          if (QueueBlocksUnsolicited[0] == header)
          {
            if (Blockchain.TryLock())
            {
              QueueBlocksUnsolicited.Remove(header);
              return false;
            }

            if (countTriesLeft-- == 0)
            {
              QueueBlocksUnsolicited.Remove(header);
              return true;
            }
          }
        }

        await Task.Delay(500).ConfigureAwait(false);
      }
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
      {
        peer = Peers.Find(p => p.TryGetBusy());
      }

      return peer != null;
    }

    public async Task AdvertizeToken(byte[] hash)
    {
      List<Peer> peers = new();

      // should Lock Blockchain

      while (true)
      {
        if (TryGetPeer(out Peer peer))
        {
          peers.Add(peer);
        }
        else if (peers.Any())
        {
          break;
        }

        await Task.Delay(1000);
      }

      peers.Select(p => p.AdvertizeToken(hash))
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

        string.Format("Received inbound request from {0}",
          tcpClient.Client.RemoteEndPoint.ToString())
          .Log(LogFile);

        var peer = new Peer(
          this, 
          Blockchain,
          Token,
          tcpClient);

        try
        {
          peer.StartMessageListener();
        }
        catch (Exception ex)
        {
          string.Format(
            "Failed to start listening to inbound peer {0}: " +
            "\n{1}: {2}",
            peer,
            ex.GetType().Name,
            ex.Message)
            .Log(LogFile);

          continue;
        }

        lock (LOCK_Peers)
        {
          Peers.Add(peer);
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
        "\n\n Status Network:\n" +
        statusPeers +
        $"\n\nCount peers: {countPeers}\n";
    }
  }
}
