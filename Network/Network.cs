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

  List<string> IPAddresses = new();


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

      lock (LOCK_Peers)
      {
        Peers.RemoveAll(p => p.IsDisposed());
        countPeersOutbound = Peers.Count(p => p.Connection == Peer.ConnectionType.OUTBOUND);
      }

      if (countPeersOutbound >= ConfigurationNetwork.CountMaxOutboundConnections)
      {
        await Task.Delay(1000 * TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS).ConfigureAwait(false);
        continue;
      }

      if (IPAddresses.Count == 0)
      {
        foreach (string seedAddress in ConfigurationNetwork.SeedAddresses)
        {
          try
          {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(seedAddress).ConfigureAwait(false);

            IPAddresses.AddRange(addresses
              .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
              .Select(x => x.ToString()));
          }
          catch
          { }
        }

        IPAddresses = IPAddresses.Distinct().ToList();

        if (IPAddresses.Count == 0)
        {
          await Task.Delay(1000).ConfigureAwait(false);
          continue;
        }
      }

      int index = Random.Shared.Next(IPAddresses.Count);

      string iP = IPAddresses[index];
      IPAddresses.RemoveAt(index);

      try
      {
        ISocketCommunication socketCommunication = Communication.GetSocketCommunication(ConfigurationNetwork.Port, iP);

        Peer peer = CreatePeer(socketCommunication, Peer.ConnectionType.OUTBOUND);

        await peer.Start(await Blockchain.RunLocked(Blockchain.GetHeight));

        lock (LOCK_Peers)
          Peers.Add(peer);
      }
      catch
      {
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