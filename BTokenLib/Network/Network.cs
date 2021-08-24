﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;


namespace BTokenLib
{
  partial class Network
  {
    Token Token;
    internal Blockchain Blockchain;

    const int UTXOIMAGE_INTERVAL_SYNC = 300;

    StreamWriter LogFile;

    const UInt16 Port = 8333;

    const int COUNT_PEERS_MAX = 8;

    object LOCK_Peers = new();
    List<Peer> Peers = new();

    object LOCK_HeadersReceivedUnsolicited = new();
    public List<Header> HeadersReceivedUnsolicited = new();

    static DirectoryInfo DirectoryLogPeers;
    static DirectoryInfo DirectoryLogPeersDisposed;



    public Network(Token token)
    {
      Token = token;

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
            p => p.FlagDispose && !p.IsBusy);

          peersDispose.ForEach(p =>
          {
            Peers.Remove(p);
            p.Dispose();
          });

          countPeersCreate = COUNT_PEERS_MAX - Peers.Count;
        }

        if (countPeersCreate > 0)
        {
          string.Format(
            "Connect with {0} new peers. " +
            "{1} peers connected currently.",
            countPeersCreate,
            Peers.Count)
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
              a => Peers.Any(p => p.GetID() == a.ToString()));
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
        string.Format(
          "{0} when creating peer {1}: \n{2}",
          ex.GetType(),
          iPAddress.ToString(),
          ex.Message)
          .Log(LogFile);

        return;
      }

      try
      {
        await peer.Connect(Port);
      }
      catch (Exception ex)
      {
        peer.SetFlagDisposed(string.Format(
          "{0} when connecting.: \n{1}",
          ex.GetType().Name,
          ex.Message));
      }

      lock (LOCK_Peers)
      {
        Peers.Add(peer);
      }
    }

    public void AddPeer()
    {
      List<IPAddress> iPAddresses = RetrieveIPAddresses(1);

      if(iPAddresses.Count < 1)
      {
        Console.WriteLine("No IP address could be retrieved.");
        return;
      }

      CreatePeer(iPAddresses[0]);
    }

    public void RemovePeer(string iPAddress)
    {
      lock (LOCK_Peers)
      {
        Peer peerRemove =
          Peers.Find(p => !p.IsBusy && p.GetID() == iPAddress);

        if(peerRemove != null)
        {
          Peers.Remove(peerRemove);
          peerRemove.Dispose();
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

    async Task StartSynchronizer()
    {
      while (true)
      {
        await Task.Delay(3000).ConfigureAwait(false);

        if (!TryGetPeerReadyToSync(out Peer peer))
        {
          continue;
        }

        if (!Blockchain.TryLock())
        {
          peer.Release();
          continue;
        }

        try
        {
          $"Start synchronization with peer {peer.GetID()}."
            .Log(LogFile);

          await peer.GetHeaders();
        }
        catch (Exception ex)
        {
          peer.SetFlagDisposed(
            $"{ex.GetType().Name} when sending getheaders.: \n{ex.Message}");
        }
      }
    }


    BufferBlock<Peer> QueueSynchronizer = new();
    bool FlagSynchronizationAbort;
    int IndexInsertion;
    Dictionary<int, BlockDownload> QueueDownloadsInsertion = new();
    List<BlockDownload> QueueDownloadsIncomplete = new();
    List<Peer> PeersDownloadingBlock = new();
    Peer PeerSynchronizationMaster;

    async Task Synchronize(Peer peer)
    {
      PeerSynchronizationMaster = peer;
      FlagSynchronizationAbort = false;
      IndexInsertion = 0;
      QueueDownloadsInsertion.Clear();
      PeersDownloadingBlock.Clear();
      QueueDownloadsIncomplete.Clear();

      int indexBlockDownload = 0;
      Header headerRoot = peer.HeaderDownload.HeaderRoot;

      while (true)
      {
        if(peer != null)
        {
          if (QueueDownloadsIncomplete.Any())
          {
            peer.BlockDownload = QueueDownloadsIncomplete.First();
            QueueDownloadsIncomplete.RemoveAt(0);

            peer.BlockDownload.Peer = peer;

            ($"peer {peer.GetID()} loads " +
               $"blockDownload {peer.BlockDownload.Index} from queue invalid.")
               .Log(LogFile);
          }
          else if (headerRoot != null)
          {
            peer.BlockDownload = new BlockDownload(
              indexBlockDownload,
              peer);

            peer.BlockDownload.LoadHeaders(
              ref headerRoot,
              peer.CountBlocksLoad);

            indexBlockDownload += 1;
          }
          else
          {
            "Synchronization completed.".Log(LogFile);

            peer.Release();
            break;
          }

          PeersDownloadingBlock.Add(peer);
          await peer.GetBlock();
        }

        if (FlagSynchronizationAbort)
        {
          string.Format("Synchronization abort.")
            .Log(LogFile);

          await Blockchain.LoadImage();

          break;
        }

        if (!TryGetPeer(out peer))
        {
          await Task.Delay(3000).ConfigureAwait(false);
        }
      }

      while (PeersDownloadingBlock.Count > 0)
      {
        "Waiting for all peers to exit block download state."
          .Log(LogFile);

        await Task.Delay(1000).ConfigureAwait(false);
      }

      lock(LOCK_Peers)
      {
        Peers.Where(p => p.IsStateAwaitingBlockDownload())
          .ToList().ForEach(p => p.Release());
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
          await Blockchain.LoadImage();
        }
      }
    }


    void PeerBlockDownloadIncomplete(
      Peer peer)
    {
      BlockDownload blockDownload = peer.BlockDownload;
      peer.BlockDownload = null;

      PeersDownloadingBlock.Remove(peer);

      blockDownload.IndexHeaderExpected = 0;
      blockDownload.Blocks.Clear();

      int indexdownload = QueueDownloadsIncomplete
        .FindIndex(b => b.Index > blockDownload.Index);

      if (indexdownload == -1)
      {
        QueueDownloadsIncomplete.Add(blockDownload);
      }
      else
      {
        QueueDownloadsIncomplete.Insert(
          indexdownload,
          blockDownload);
      }
    }

    void InsertBlockDownload(Peer peer)
    {
      BlockDownload blockDownload = peer.BlockDownload;
      peer.BlockDownload = null;

      if (IndexInsertion == blockDownload.Index)
      {
        while (true)
        {
          if (!TryInsertBlockDownload(blockDownload))
          {
            blockDownload.Peer.SetFlagDisposed(
              $"Insertion of block download {blockDownload.Index} failed. " +
              "Abort flag is set.");

            FlagSynchronizationAbort = true;

            break;
          }

          ($"Insert block download {blockDownload.Index}. " +
            $"Blockchain height: {Blockchain.HeaderTip.Height}")
            .Log(LogFile);

          IndexInsertion += 1;

          if (!QueueDownloadsInsertion.TryGetValue(
            IndexInsertion,
            out blockDownload))
          {
            break;
          }

          QueueDownloadsInsertion.Remove(IndexInsertion);
        }
      }
      else
      {
        QueueDownloadsInsertion.Add(
          blockDownload.Index,
          blockDownload);
      }

      PeersDownloadingBlock.Remove(peer);
    }

    bool TryInsertBlockDownload(BlockDownload blockDownload)
    {
      try
      {
        foreach (Block block in blockDownload.Blocks)
        {
          Blockchain.InsertBlock(
              block,
              flagValidateHeader: false);

          Blockchain.ArchiveBlock(
              block,
              UTXOIMAGE_INTERVAL_SYNC);
        }

        return true;
      }
      catch
      {
        return false;
      }
    }

    bool TryGetPeerReadyToSync(out Peer peer)
    {
      lock (LOCK_Peers)
      {
        peer = Peers.Find(p =>
         !p.IsSynchronized &&
         !p.FlagDispose &&
         !p.IsBusy);

        if (peer != null)
        {
          peer.IsBusy = true;
          return true;
        }

        return false;
      }
    }

    bool TryGetPeer(out Peer peer)
    {
      lock (LOCK_Peers)
      {
        peer = Peers.Find(
          p => !p.FlagDispose && !p.IsBusy);

        if (peer != null)
        {
          Debug.WriteLine(string.Format(
            "Network gets peer {0}",
            peer.GetID()));

          peer.IsBusy = true;
          return true;
        }
      }

      return false;
    }

    public async Task AdvertizeToken(byte[] hash)
    {
      var peers = new List<Peer>();

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

    public bool IsHeaderUnsolicitedDuplicate(Header header)
    {
      lock(LOCK_HeadersReceivedUnsolicited)
      {
        if(HeadersReceivedUnsolicited.Any(
          h => h.Hash.IsEqual(header.Hash)))
        {
          return true;
        }

        HeadersReceivedUnsolicited.Add(header);
        return false;
      }
    }

    public void ClearHeaderUnsolicitedDuplicates()
    {
      lock (LOCK_HeadersReceivedUnsolicited)
      {
        HeadersReceivedUnsolicited.Clear();
      }
    }


    const int PEERS_COUNT_INBOUND = 8;
    TcpListener TcpListener =
      new TcpListener(IPAddress.Any, Port);

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
            peer.GetID(),
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
  }
}
