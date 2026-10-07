using System.Net;
using System.Net.Sockets;


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
  }

  internal void Start()
  {
    StartPeerConnectorOutbound();

    if (ConfigNetwork.EnableInboundConnections)
      StartPeerConnectorInbound();

    StartBlockDownloadDispatcher();
  }

  internal void Broadcast(TX tX)
  {
    lock (LOCK_Peers)
      foreach (Peer peer in Peers)
        peer.BroadcastTX(tX);
  }

  internal void AnnounceHeader(Block block)
  {
    if (!block.Header.IsPlaceholderNewest())
      return;

    lock (LOCK_Peers)
      Peers.ForEach(p => HeadersMessage.SendHeaders(
        p,
        new List<byte[]> { block.Header.Serialize() }));
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

    Block blockDownload = new(Blockchain.Token);
    Block blockUpload = new(Blockchain.Token);

    protocol.Add(GetDataMessage.Command, new GetDataMessage(Blockchain, blockUpload));
    protocol.Add(HeadersMessage.Command, new HeadersMessage(Blockchain));
    protocol.Add(BlockMessage.Command, new BlockMessage(Blockchain, blockDownload));
    protocol.Add(VerAckMessage.Command, new VerAckMessage(Blockchain));
    protocol.Add(VersionMessage.Command, new VersionMessage(Blockchain));
    protocol.Add(PingMessage.Command, new PingMessage());
    protocol.Add(NotFoundMessage.Command, new NotFoundMessage());
    protocol.Add(UnknownMessage.Command, new UnknownMessage());

    if (ConfigNetwork.IsProtocolBitcoin)
    {
      protocol.Add(GetHeadersMessage.Command, new GetHeadersMessage(Blockchain));
      protocol.Add(InvMessage.Command, new InvMessage(Blockchain));
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
        if (peer.IsDisposed() || !peer.SemaphorePeer.Wait(0))
          continue;

        try
        {
          BlockMessage blockMessage = (BlockMessage)peer.ProtocolStateMachine[BlockMessage.Command];
          HeadersMessage headersMessage = (HeadersMessage)peer.ProtocolStateMachine[HeadersMessage.Command];

          if (blockMessage.BlockDownload.Header == null)
          {
            if (peer.StateCurrent != Peer.StateProtocol.Idle)
              continue;

            Header headerDownload = await Blockchain.GetHeaderDownload(
              headersMessage.HeaderTipReceivedLast,
              peer.HashesBlockRefused,
              peer.HashBlockAnnounced);

            if (headerDownload != null)
              await blockMessage.SendBlockRequest(peer, headerDownload);
          }
          else if (DateTime.UtcNow - blockMessage.TimeRequestBlock > TimeSpan.FromSeconds(TIMEOUT_BLOCK_REQUEST_SECONDS))
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