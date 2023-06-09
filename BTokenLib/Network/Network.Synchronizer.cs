using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  partial class Network
  {
    const int TIME_LOOP_SYNCHRONIZER_SECONDS = 30;

    readonly object LOCK_IsStateSynchronizing = new();
    bool IsStateSynchronizing;
    Peer PeerSynchronizing;
    HeaderDownload HeaderDownload;

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

    object LOCK_ChargeHeader = new();
    ConcurrentBag<Block> PoolBlocks = new();

    object LOCK_ChargeHashDB = new();
    List<byte[]> QueueHashesDBDownloadIncomplete = new();


    async Task StartSynchronizerLoop()
    {
      Random randomGenerator = new();

      while (true)
      {
        int timespanRandomSeconds = TIME_LOOP_SYNCHRONIZER_SECONDS / 2 
          + randomGenerator.Next(TIME_LOOP_SYNCHRONIZER_SECONDS);

        await Task.Delay(timespanRandomSeconds * 1000)
          .ConfigureAwait(false);

        TryStartSynchronization();
      }
    }

    public bool TryStartSynchronization()
    {
      return TryStartSynchronization(null);
    }
    bool TryStartSynchronization(Peer peerSync)
    {
      lock (LOCK_IsStateSynchronizing)
      {
        if (IsStateSynchronizing)
          return false;

        lock (LOCK_Peers)
        {
          foreach (Peer p in Peers)
            if (peerSync == null)
            {
              if (p.TrySync())
                peerSync = p;
            }
            else if (p.TrySync(peerSync))
            {
              peerSync.SetStateIdle();
              peerSync = p;
            }

          if (peerSync == null)
            return false;
        }

        if (!Token.TryLock())
        {
          peerSync.SetStateIdle();
          return false;
        }

        EnterStateSynchronization(peerSync);
      }

      peerSync.SendGetHeaders(HeaderDownload.Locator);
      return true;
    }

    bool TryEnterStateSynchronization(Peer peer)
    {
      lock (LOCK_IsStateSynchronizing)
      {
        if (IsStateSynchronizing)
          return false;

        if (!Token.TryLock())
          return false;

        EnterStateSynchronization(peer);

        return true;
      }
    }

    void EnterStateSynchronization(Peer peer)
    {
      $"Enter state synchronzation with peer {peer}.".Log(this, LogFile);

      peer.SetStateHeaderSynchronization();
      PeerSynchronizing = peer;
      IsStateSynchronizing = true;
      HeaderDownload = new HeaderDownload(Token.GetLocator());
    }

    void ExitSynchronization()
    {
      IsStateSynchronizing = false;
      PeerSynchronizing.SetStateIdle();
      PeerSynchronizing.TimeLastSynchronization = DateTime.Now;
      PeerSynchronizing = null;
      Token.ReleaseLock();
    }

    void HandleExceptionPeerListener(Peer peer)
    {
      lock (LOCK_IsStateSynchronizing) lock (LOCK_Peers)
        {
          if (IsStateSynchronizing && PeerSynchronizing == peer)
            ExitSynchronization();
          else if (peer.IsStateBlockSynchronization())
            ReturnPeerBlockDownloadIncomplete(peer);
          else if (peer.IsStateDBDownload())
            ReturnPeerDBDownloadIncomplete(peer.HashDBDownload);

          Peers.Remove(peer);
        }
    }

    async Task SyncBlocks()
    {
      double difficultyOld = Token.HeaderTip.Difficulty;

      try
      {
        if (HeaderDownload.HeaderTip != null)
          if (HeaderDownload.HeaderTip.DifficultyAccumulated > Token.HeaderTip.DifficultyAccumulated)
          {
            if (HeaderDownload.HeaderAncestor != Token.HeaderTip)
            {
              ($"HeaderDownload.HeaderAncestor {HeaderDownload.HeaderAncestor} " +
                $"not equal to {Token.HeaderTip}").Log(LogFile);

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
                  peer.RequestBlock();
                else
                {
                  peer.SetStateIdle();

                  if (Peers.All(p => !p.IsStateBlockSynchronization()))
                  {
                    if (
                      difficultyOld > 0 &&
                      Token.HeaderTip.Difficulty > difficultyOld)
                    {
                      Token.Reorganize();
                    }

                    break;
                  }
                }

              TryGetPeerIdle(out peer);

              await Task.Delay(1000).ConfigureAwait(false);
            }
          }
          else if(HeaderDownload.HeaderTip.DifficultyAccumulated < Token.HeaderTip.DifficultyAccumulated)
            PeerSynchronizing.SendHeaders(new List<Header>() { Token.HeaderTip });

        $"Synchronization with {PeerSynchronizing} of {Token.GetName()} completed.".Log(LogFile);
      }
      catch (Exception ex)
      {
        ($"Unexpected exception {ex.GetType().Name} occured during SyncBlocks.\n" +
          $"{ex.Message}").Log(LogFile);
      }

      Token.GetStatus().Log(LogFile);
      ExitSynchronization();

      if(Token.TokenChild != null)
        Token.TokenChild.Network.TryStartSynchronization();
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

              $"Inserted block {Token.HeaderTip.Height}, {block}."
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

        TryGetPeerIdle(out peer);

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
  }
}
