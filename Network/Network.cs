using System.Net;
using System.Net.Sockets;


namespace BTokenCore;

internal abstract partial class Network
{
  protected Blockchain Blockchain;
  Configuration ConfigurationNetwork;

  ICommunication Communication;

  const int COUNT_MAX_OUTBOUND_CONNECTIONS = 1;
  const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 5;
  const int COUNT_MAX_INBOUND_CONNECTIONS = 1;

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

    if (ConfigurationNetwork.EnableInboundConnections)
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

        ISocketCommunication socketCommunication = Communication.GetSocketCommunication(ConfigurationNetwork.Port, iP);

        Peer peer = CreatePeer(socketCommunication, Peer.ConnectionType.OUTBOUND);

        await peer.Start(await Blockchain.RunLocked(Blockchain.GetHeight));

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
      foreach (string dnsSeed in ConfigurationNetwork.SeedAddresses)
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
    Communication.StartListenerCommunicationInbound(ConfigurationNetwork.Port);

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