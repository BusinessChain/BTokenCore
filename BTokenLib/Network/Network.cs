﻿using System;
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
    const int TIMEOUT_RESPONSE_MILLISECONDS = 10000;

    StreamWriter LogFile;

    const UInt16 Port = 8333;

    int CountPeersMax = 1;// Math.Max(Environment.ProcessorCount - 1, 4);

    object LOCK_Peers = new();
    List<Peer> Peers = new();
    ConcurrentBag<BlockDownload> PoolBlockDownload = new();
    List<Block> BlocksCached = new();

    static readonly DirectoryInfo DirectoryLogPeers =
      Directory.CreateDirectory("logPeers");

    static readonly DirectoryInfo DirectoryLogPeersDisposed =
      Directory.CreateDirectory(
        Path.Combine(
          DirectoryLogPeers.FullName,
          "disposed"));



    public Network(Token token)
    {
      Token = token;
      Blockchain = token.Blockchain;

      string pathRoot = token.GetName();

      LogFile = new StreamWriter(
        Path.Combine(pathRoot, "LogNetwork"),
        false);

      LoadNetworkConfiguration(pathRoot);
    }


    public void Start()
    {
      StartPeerConnector(); 

      StartSync();

      StartPeerInboundListener(); 
      // Falls einer anfragt, wird angenommen, dass er nur 
      // Bitcoin versteht, falls er nicht aktiv nach BToken fragt.
    }

    void LoadNetworkConfiguration (string pathConfigFile)
    {
      "Load Network configuration.".Log(LogFile);
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

          List<string> iPAddresses = Token.RetrieveIPAdresses(
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
            $"No ip address found to connect in protocol {Token}.".Log(LogFile);

          LABEL_DelayAndContinue:

          await Task.Delay(10000).ConfigureAwait(false);
        }
      }
      catch (Exception ex)
      {
        $"{ex.GetType().Name} in StartPeerConnector of protocol {Token}.".Log(LogFile);
      }
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
        Peers.ForEach(p => p.FlagSyncStaged = true);
    }


    Peer PeerSync;

    async Task StartSync()
    {
      while (true)
      {
        await Task.Delay(5000).ConfigureAwait(false);

        lock (LOCK_Peers)
        {
          PeerSync = Peers.Find(p => p.TrySync());

          if (PeerSync == null)
            continue;
        }

        if (!Token.TryLock())
        {
          PeerSync.Release();
          continue;
        }  

        $"Start synchronization of {Token.GetName()} with peer {PeerSync}."
          .Log(LogFile);

        try
        {
          await PeerSync.GetHeaders(Blockchain);
        }
        catch (Exception ex)
        {
          PeerSync.SetFlagDisposed(
            $"{ex.GetType().Name} in getheaders: \n{ex.Message}");

          Token.ReleaseLock();
          continue;
        }
      }
    }

    bool FlagSyncAbort;
    int IndexInsertion;
    int IndexBlockDownload;
    object LOCK_InsertBlockDownload = new();
    Dictionary<int, BlockDownload> QueueDownloadsInsertion = new();
    List<BlockDownload> QueueDownloadsIncomplete = new();
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
          Blockchain.LoadImage(headerDownload.HeaderAncestor.Height);
        }

        FlagSyncAbort = false;
        IndexInsertion = 0;
        QueueDownloadsInsertion.Clear();
        QueueDownloadsIncomplete.Clear();

        IndexBlockDownload = 0;
        HeaderRoot = headerDownload.HeaderRoot;

        Peer peer = PeerSync;

        while (true)
        {
          if (FlagSyncAbort)
          {
            "Synchronization was abort. Reload Image.".Log(LogFile);
            Blockchain.LoadImage();

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
            if (TryChargeBlockDownload(peer))
              await peer.GetBlock();
            else if (Peers.Any(p => p.IsStateBlockDownload()))
              peer.Release();
            else
            {
              if(
                difficultyOld > 0 && 
                Blockchain.HeaderTip.Difficulty > difficultyOld)
              {
                Blockchain.Reorganize();
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

      $"Synchronization of {Token.GetName()} completed.".Log(LogFile);

      PeerSync.Release();

      Token.ReleaseLock();
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
            blockDownload.Headers = BlockDownloadBlocking.Headers.ToList();
            blockDownload.IndexHeaders = 0;
            blockDownload.BlockDownloadIndexPrevious = BlockDownloadBlocking.BlockDownloadIndexPrevious;

            peer.BlockDownload = blockDownload;
            return true;
          }
          else
            peer.BlockDownload = null;
        }
        else if (blockDownload.Index == IndexInsertion)
        {
          bool flagReturnBlockDownloadToPool = false;

          while (true)
          {
            blockDownload.Peer.CountInsertBlockDownload++;

            try
            {
              for (int i = 0; i < blockDownload.Headers.Count; i += 1)
                Blockchain.InsertBlock(
                  blockDownload.Blocks[i],
                  flagCreateImage: blockDownload.Index % UTXOIMAGE_INTERVAL_SYNC == 0);

              ($"Inserted blockDownload {blockDownload.Index} from peer " +
                $"{blockDownload.Peer}. Height: {Blockchain.HeaderTip.Height}.")
              .Log(LogFile);
            }
            catch (Exception ex)
            {
              blockDownload.Peer.SetFlagDisposed(
                $"Insertion of block download {blockDownload.Index} failed:\n" +
                $"{ex.Message}.");

              FlagSyncAbort = true;

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
          peer.CountWastedBlockDownload++;
      }

      return TryChargeBlockDownload(peer);
    }

    object LOCK_ChargeFromHeaderRoot = new();

    bool TryChargeBlockDownload(Peer peer)
    {
      BlockDownload blockDownload = peer.BlockDownload;

      lock(QueueDownloadsIncomplete)
        if (QueueDownloadsIncomplete.Any())
        {
          if (blockDownload != null)
            PoolBlockDownload.Add(blockDownload);

          blockDownload = QueueDownloadsIncomplete.First();
          QueueDownloadsIncomplete.RemoveAt(0);

          blockDownload.Peer = peer;

          peer.BlockDownload = blockDownload;

          return true;
        }

      lock (LOCK_ChargeFromHeaderRoot)
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

      return false;
    }

    void ReturnPeerBlockDownloadIncomplete(Peer peer)
    {
      lock (LOCK_InsertBlockDownload)
        if (BlockDownloadBlocking == peer.BlockDownload)
          return;

      BlockDownload blockDownload = peer.BlockDownload;

      peer.BlockDownload = null;

      blockDownload.Peer = null;
      blockDownload.IndexHeaders = 0;
      blockDownload.CountBytes = 0;

      lock(QueueDownloadsIncomplete)
      {
        int indexInsertInQueue = QueueDownloadsIncomplete
          .FindIndex(b => blockDownload.Index < b.Index);

        if (indexInsertInQueue == -1)
          QueueDownloadsIncomplete.Add(blockDownload);
        else
          QueueDownloadsIncomplete.Insert(
            indexInsertInQueue,
            blockDownload);
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
      await Task.Delay(300).ConfigureAwait(false);

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

    public async Task AdvertizeToken(byte[] hash)
    {
      List<Peer> peers = new();

      // should Lock Blockchain

      while (true)
      {
        if (TryGetPeer(out Peer peer))
          peers.Add(peer);
        else if (peers.Any())
          break;

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
