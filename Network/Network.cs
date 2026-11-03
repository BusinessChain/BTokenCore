using System.Net;
using System.Net.Sockets;


namespace BTokenCore;

internal abstract partial class Network
{
  protected Blockchain Blockchain;
  Configuration ConfigurationNetwork;

  ICommunication Communication;

  const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 5;

  protected object LOCK_Peers = new();
  protected List<Peer> Peers = new();

  HashSet<string> IPAddresses = new();
  const int COUNT_MAX_IP_ADDRESSES = 10_000;


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
        Peers.RemoveAll(p => p.IsDisposed());
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
              IPAddresses.UnionWith(addresses
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                .Select(x => x.ToString()));
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

        lock (LOCK_Peers)
          if (Peers.Any(p => p.GetIP().Equals(socketCommunication.GetIP()))
            || Peers.Count(p => p.Connection == Peer.ConnectionType.INBOUND) + 1 > ConfigurationNetwork.CountMaxInboundConnections)
          {
            throw new ProtocolException("Inbound request rejected.");
          }

        Peer peer = CreatePeer(socketCommunication, Peer.ConnectionType.INBOUND);

        await peer.Start(await Blockchain.RunLocked(Blockchain.GetHeight));

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

  protected abstract Peer CreatePeer(
    ISocketCommunication socketCommunication,
    Peer.ConnectionType connection);
}