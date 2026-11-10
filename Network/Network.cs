using System.Net;


namespace BTokenCore;

internal abstract partial class Network
{
  internal Blockchain Blockchain;
  internal Configuration ConfigurationNetwork;

  ICommunication Communication;

  const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 5;
  internal const int TIMEOUT_REQUEST_SECONDS = 60;

  protected object LOCK_Peers = new();
  protected List<Peer> Peers = new();
  List<Peer> PeersHandshakeInbound = new();

  HashSet<string> IPAddresses = new();
  const int COUNT_MAX_IP_ADDRESSES = 10_000;

  const int COUNT_PEERS_PROTECTED_PER_CRITERION = 4;


  protected Network(
    Blockchain blockchain,
    ICommunication communication,
    Configuration configNetwork)
  {
    Blockchain = blockchain;
    Communication = communication;
    ConfigurationNetwork = configNetwork;

    Blockchain.OnBlockInserted += HandleBlockInserted;
  }

  internal void Start()
  {
    StartPeerConnectorOutbound();

    if (ConfigurationNetwork.CountMaxInboundConnections > 0)
      StartPeerConnectorInbound();
  }

  void HandleBlockInserted(Block block)
  {
    lock (LOCK_Peers)
      foreach (Peer peer in Peers)
        if (block.Header.IsParentNewest())
          HeadersMessage.SendHeaders(peer, new List<byte[]> { block.Header.Serialize() });
  }

  async Task StartPeerConnectorOutbound()
  {
    while (true)
    {
      int countPeersOutbound;
      List<string> iPsCandidate;

      lock (LOCK_Peers)
      {
        countPeersOutbound = Peers.Count(p => p.Connection == Peer.ConnectionType.OUTBOUND);
        iPsCandidate = IPAddresses.Except(Peers.Select(p => p.GetIP())).ToList();
      }

      if (countPeersOutbound >= ConfigurationNetwork.CountMaxOutboundConnections)
      {
        await Task.Delay(1000 * TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS).ConfigureAwait(false);
        continue;
      }

      if (iPsCandidate.Count == 0)
      {
        foreach (string seedAddress in ConfigurationNetwork.SeedAddresses)
        {
          try
          {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(seedAddress).ConfigureAwait(false);

            lock (LOCK_Peers)
              IPAddresses.UnionWith(addresses.Select(x => x.ToString()));
          }
          catch
          { }
        }

        await Task.Delay(1000).ConfigureAwait(false);
        continue;
      }

      string iP = iPsCandidate[Random.Shared.Next(iPsCandidate.Count)];

      ISocketCommunication socketCommunication = null;

      try
      {
        socketCommunication = Communication.GetSocketCommunication(ConfigurationNetwork.Port, iP);

        Peer peer = CreatePeer(socketCommunication, Peer.ConnectionType.OUTBOUND);

        await peer.Start(await Blockchain.RunLocked(Blockchain.GetHeight));

        lock (LOCK_Peers)
          if (!peer.IsDisposed)
            Peers.Add(peer);
      }
      catch
      {
        socketCommunication?.Dispose();

        lock (LOCK_Peers)
          IPAddresses.Remove(iP);

        await Task.Delay(1000).ConfigureAwait(false);
      }
    }
  }

  async Task StartPeerConnectorInbound()
  {
    Communication.StartListenerCommunicationInbound(ConfigurationNetwork.Port);

    while (true)
    {
      ISocketCommunication socketCommunication = null;

      try
      {
        socketCommunication = await Communication.AcceptSocketCommunicationInbound();

        string iP = socketCommunication.GetIP();

        Peer peer;

        lock (LOCK_Peers)
        {
          if (Peers.Concat(PeersHandshakeInbound).Any(p => p.GetIP().Equals(iP)))
            throw new ProtocolException("Inbound request rejected.");

          if (Peers.Count(p => p.Connection == Peer.ConnectionType.INBOUND) + PeersHandshakeInbound.Count >= ConfigurationNetwork.CountMaxInboundConnections)
            EvictPeerInbound();

          peer = CreatePeer(socketCommunication, Peer.ConnectionType.INBOUND);
          PeersHandshakeInbound.Add(peer);
        }

        StartPeerInbound(peer);
      }
      catch
      {
        socketCommunication?.Dispose();
      }
    }
  }

  void EvictPeerInbound()
  {
    List<Peer> peersCandidate = Peers.Where(p => p.Connection == Peer.ConnectionType.INBOUND).ToList();

    List<Peer> peersBlockDelivered = peersCandidate
      .Where(p => ((BlockMessage)p.MessagesByCommand[BlockMessage.Command]).TimeDeliveredLast > DateTime.MinValue)
      .ToList();

    List<Peer> peersTXDelivered = peersCandidate
      .Where(p => ((TXMessage)p.MessagesByCommand[TXMessage.Command]).TimeDeliveredLast > DateTime.MinValue)
      .ToList();

    HashSet<Peer> peersProtected = peersBlockDelivered
      .OrderByDescending(p => ((BlockMessage)p.MessagesByCommand[BlockMessage.Command]).TimeDeliveredLast)
      .Take(COUNT_PEERS_PROTECTED_PER_CRITERION)
      .Concat(peersTXDelivered
        .OrderByDescending(p => ((TXMessage)p.MessagesByCommand[TXMessage.Command]).TimeDeliveredLast)
        .Take(COUNT_PEERS_PROTECTED_PER_CRITERION))
      .Concat(peersBlockDelivered.Union(peersTXDelivered)
        .OrderBy(p => p.TimeConnected)
        .Take(COUNT_PEERS_PROTECTED_PER_CRITERION))
      .ToHashSet();

    List<Peer> peersEvictable = peersCandidate.Except(peersProtected).ToList();

    peersEvictable[Random.Shared.Next(peersEvictable.Count)].SocketCommunication.Dispose();
  }

  async Task StartPeerInbound(Peer peer)
  {
    try
    {
      await peer.Start(await Blockchain.RunLocked(Blockchain.GetHeight));

      lock (LOCK_Peers)
        if (!peer.IsDisposed)
          Peers.Add(peer);
    }
    catch
    {
      peer.SocketCommunication.Dispose();
    }
    finally
    {
      lock (LOCK_Peers)
        PeersHandshakeInbound.Remove(peer);
    }
  }

  protected abstract Peer CreatePeer(
    ISocketCommunication socketCommunication,
    Peer.ConnectionType connection);
}