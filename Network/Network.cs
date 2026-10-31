using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class Network
{
  Blockchain Blockchain;
  ConfigNetwork ConfigNetwork;

  ICommunication Communication;

  const int COUNT_MAX_OUTBOUND_CONNECTIONS = 1;
  const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 5;
  const int COUNT_MAX_INBOUND_CONNECTIONS = 1;

  const int TIMESPAN_LOOP_DISPATCHER_MILLISECONDS = 1000;
  const int TIMESPAN_MEAN_INV_SECONDS = 5;
  const int TIMEOUT_BLOCK_REQUEST_SECONDS = 60;

  object LOCK_Peers = new();
  List<Peer> Peers = new();

  List<string> IPAddresses = new();


  internal Network(
    Blockchain blockchain,
    ICommunication communication,
    ConfigNetwork configNetwork)
  {
    Blockchain = blockchain;
    Communication = communication;
    ConfigNetwork = configNetwork;

    Blockchain.OnBlockInserted += HandleBlockInserted;
  }

  internal void Start()
  {
    StartPeerConnectorOutbound();

    if (ConfigNetwork.EnableInboundConnections)
      StartPeerConnectorInbound();

    StartBlockDownloadDispatcher();
  }

  internal async Task SubmitTX(byte[] tXRaw)
  {
    using SHA256 sHA256 = SHA256.Create();

    int index = 0;

    TX tX = Blockchain.ParseTX(tXRaw, ref index, sHA256, flagIsCoinbase: false);

    await ((BlockchainBToken)Blockchain).AddTX(tX);

    Broadcast(tX);
  }

  internal void Broadcast(TX tX)
  {
    lock (LOCK_Peers)
      foreach (Peer peer in Peers)
        // Reto: a future child-token anchor on the BToken network must bypass the batching as well.
        if (ConfigNetwork.IsProtocolBitcoin)
        {
          lock (peer.HashesTXAnnounced)
            peer.HashesTXAnnounced.Add(tX.Hash);

          NetworkMessage.SendInventories(peer, InvMessage.Command, [new(Inventory.InventoryType.MSG_TX, tX.Hash)]);
        }
        else
          lock (peer.HashesTXQueued)
            peer.HashesTXQueued.Add(tX.Hash);
  }

  void HandleBlockInserted(Block block)
  {
    lock (LOCK_Peers)
      foreach (Peer peer in Peers)
      {
        lock (peer.HashesTXAnnounced)
          peer.HashesTXAnnounced.Clear();

        if (block.Header.IsParentNewest())
          HeadersMessage.SendHeaders(peer, new List<byte[]> { block.Header.Serialize() });
      }
  }

  async Task StartPeerConnectorOutbound()
  {
    while (true)
    {
      int countPeers;

      lock (LOCK_Peers)
      {
        Peers.RemoveAll(p => p.IsDisposed());
        countPeers = Peers.Count;
      }

      if (countPeers < COUNT_MAX_OUTBOUND_CONNECTIONS)
      {
        Peer peer = await GetPeer();

        lock (LOCK_Peers)
          Peers.Add(peer);
      }
      else
        await Task.Delay(1000 * TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS).ConfigureAwait(false);
    }
  }

  async Task<Peer> GetPeer()
  {
    while (true)
    {
      try
      {
        //string iP = GetIPAddress();

        string iP = "83.229.86.158"; // 84.74.69.100

        ISocketCommunication socketCommunication = Communication.GetSocketCommunication(ConfigNetwork.Port, iP);

        Peer peer = new(this, socketCommunication, Peer.ConnectionType.OUTBOUND);

        await peer.Start(Blockchain.GetHeight());

        return peer;
      }
      catch
      {
        await Task.Delay(1000);
      }
    }
  }

  string GetIPAddress()
  {
    while (IPAddresses.Count == 0)
    {
      foreach (string dnsSeed in ConfigNetwork.SeedAddresses)
      {
        try
        {
          IPAddress[] addresses = Dns.GetHostAddresses(dnsSeed);

          IPAddresses.AddRange(addresses
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
            .Select(x => x.ToString()));
        }
        catch
        { }
      }

      IPAddresses = IPAddresses.Distinct().ToList();

      if (IPAddresses.Count == 0)
        Thread.Sleep(1000);
    }

    int index = Random.Shared.Next(IPAddresses.Count);

    string ip = IPAddresses[index];
    IPAddresses.RemoveAt(index);

    return ip;
  }

  async Task StartPeerConnectorInbound()
  {
    Communication.StartListenerCommunicationInbound(ConfigNetwork.Port);

    while (true)
    {
      ISocketCommunication socketCommunication = null;

      try
      {
        socketCommunication = await Communication.AcceptSocketCommunicationInbound();

        lock (LOCK_Peers)
          if (Peers.Any(p => p.GetIP().Equals(socketCommunication.GetIP()))
            || Peers.Count(p => p.Connection == Peer.ConnectionType.INBOUND) + 1 > COUNT_MAX_INBOUND_CONNECTIONS)
          {
            throw new ProtocolException("Inbound request rejected.");
          }

        Peer peer = new(this, socketCommunication, Peer.ConnectionType.INBOUND);

        await peer.Start(Blockchain.GetHeight());

        lock (LOCK_Peers)
          Peers.Add(peer);
      }
      catch
      {
        socketCommunication?.Dispose();

        await Task.Delay(30_000).ConfigureAwait(false);
      }
    }
  }

  Dictionary<string, NetworkMessage> CreateStateMachineProtocol()
  {
    Dictionary<string, NetworkMessage> protocol = new();

    Block blockDownload = new(Blockchain);
    Block blockUpload = new(Blockchain);

    protocol.Add(GetDataMessage.Command, new GetDataMessage(Blockchain, blockUpload));
    protocol.Add(HeadersMessage.Command, new HeadersMessage(Blockchain));
    protocol.Add(BlockMessage.Command, new BlockMessage(Blockchain, blockDownload));
    protocol.Add(VerAckMessage.Command, new VerAckMessage(Blockchain));
    protocol.Add(VersionMessage.Command, new VersionMessage(Blockchain));
    protocol.Add(PingMessage.Command, new PingMessage());
    protocol.Add(PongMessage.Command, new PongMessage());
    protocol.Add(NotFoundMessage.Command, new NotFoundMessage());
    protocol.Add(UnknownMessage.Command, new UnknownMessage());
    protocol.Add(InvMessage.Command, new InvMessage(Blockchain));

    if (ConfigNetwork.IsProtocolBitcoin)
      protocol.Add(GetHeadersMessage.Command, new GetHeadersMessage(Blockchain));
    else
    {
      protocol.Add(DBMessage.Command, new DBMessage((BlockchainBToken)Blockchain));
      protocol.Add(TXMessage.Command, new TXMessage((BlockchainBToken)Blockchain, this));
    }

    return protocol;
  }

  async Task StartBlockDownloadDispatcher()
  {
    while (true)
    {
      await Task.Delay(TIMESPAN_LOOP_DISPATCHER_MILLISECONDS).ConfigureAwait(false);

      List<Peer> peers;

      lock (LOCK_Peers)
        peers = Peers.ToList();

      foreach (Peer peer in peers)
      {
        if (peer.IsDisposed())
          continue;

        if (peer.StateCurrent != Peer.StateProtocol.Handshake)
          try
          {
            await ((PingMessage)peer.ProtocolStateMachine[PingMessage.Command]).Ping(peer);

            if (DateTime.UtcNow >= peer.TimeInvNext)
            {
              HashSet<byte[]> hashesQueued;

              lock (peer.HashesTXQueued)
                hashesQueued = new(peer.HashesTXQueued, new EqualityComparerByteArray());

              List<byte[]> hashesTX = new();

              if (hashesQueued.Count > 0)
              {
                List<BlockchainBToken.TXBToken> tXsRanked = await ((BlockchainBToken)Blockchain).GetTXsRanked(hashesQueued);

                foreach (BlockchainBToken.TXBToken tX in tXsRanked)
                {
                  if (hashesTX.Count == InvMessage.MAX_COUNT_INVENTORIES || !peer.ThrottleTXAnnouncedToPeer.TryIncrement(tX.CountBytes))
                    break;

                  hashesTX.Add(tX.Hash);
                }

                hashesQueued.ExceptWith(tXsRanked.Skip(hashesTX.Count).Select(t => t.Hash));

                lock (peer.HashesTXQueued)
                  peer.HashesTXQueued.ExceptWith(hashesQueued);
              }

              if (hashesTX.Count > 0)
              {
                lock (peer.HashesTXAnnounced)
                  peer.HashesTXAnnounced.UnionWith(hashesTX);

                await NetworkMessage.SendInventories(
                  peer,
                  InvMessage.Command,
                  hashesTX.Select(h => new Inventory(Inventory.InventoryType.MSG_TX, h)).ToList());

                peer.TimeInvNext = DateTime.UtcNow.AddSeconds(
                  -TIMESPAN_MEAN_INV_SECONDS * Math.Log(1 - Random.Shared.NextDouble()));
              }
            }
          }
          catch
          {
            peer.SocketCommunication.Dispose();
          }

        if (!peer.SemaphorePeer.Wait(0))
          continue;

        try
        {
          BlockMessage blockMessage = (BlockMessage)peer.ProtocolStateMachine[BlockMessage.Command];

          if (blockMessage.BlockDownload.Header == null && peer.StateCurrent != Peer.StateProtocol.DBDownload)
          {
            if (peer.StateCurrent != Peer.StateProtocol.Idle)
              continue;

            await blockMessage.RequestBlockNext(peer);
          }
          else if (DateTime.UtcNow - blockMessage.TimeRequest > TimeSpan.FromSeconds(TIMEOUT_BLOCK_REQUEST_SECONDS))
            peer.SocketCommunication.Dispose();
        }
        catch
        {
          peer.SocketCommunication.Dispose();
        }
        finally
        {
          peer.SemaphorePeer.Release();
        }
      }
    }
  }
}