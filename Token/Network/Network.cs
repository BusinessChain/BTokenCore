using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

using LiteDB;


namespace BTokenCore;

internal partial class Network
{
  internal Network NetworkParent;
  internal List<Network> NetworksChild = new();

  internal Token Token;

  internal ICommunication Communication;

  internal LiteDatabase LiteDatabase;
  internal ILiteCollection<BsonDocument> DatabaseHeaderCollection;
  internal ILiteCollection<BsonDocument> DatabaseBlockCollection;
  internal ILiteCollection<BsonDocument> DatabaseBlockMinedCollection;

  const int COUNT_MAX_OUTBOUND_CONNECTIONS = 1;
  const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 5;
  const int COUNT_MAX_INBOUND_CONNECTIONS = 1;

  bool EnableInboundConnections;
  bool EnableRelay;

  object LOCK_Peers = new();
  internal List<Peer> Peers = new();

  internal enum ConnectionType { OUTBOUND, INBOUND };
  internal List<string> IPAddresses = new();


  internal Network(
    ICommunication communication,
    Token tokenParent,
    Token token,
    bool flagEnableInboundConnections,
    bool flagEnableRelay)
  {
    Communication = communication;

    NetworkParent = tokenParent?.Network;
    Token = token;

    BlockchainRoot = new();

    EnableInboundConnections = flagEnableInboundConnections;
    EnableRelay = flagEnableRelay;

    LiteDatabase = new LiteDatabase($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    DatabaseHeaderCollection = LiteDatabase.GetCollection<BsonDocument>("headers");
    DatabaseBlockCollection = LiteDatabase.GetCollection<BsonDocument>("blocks");
    DatabaseBlockMinedCollection = LiteDatabase.GetCollection<BsonDocument>("blocksMined");
  }

  internal void Start()
  {
    LoadBlockchain();

    StartPeerConnectorOutbound();

    if (EnableInboundConnections)
      StartPeerConnectorInbound();
  }

  internal void StartMiner()
  {
    IsMining = true;
  }

  internal void StopMiner()
  {
    IsMining = true;
  }

  async Task StartPeerConnectorOutbound()
  {
    while (true)
    {
      Peers.RemoveAll(p => p.IsDisposed());

      if(Peers.Count < COUNT_MAX_OUTBOUND_CONNECTIONS)
        Peers.Add(await GetPeer(Token));
      else
        await Task.Delay(1000 * TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS).ConfigureAwait(false);
    }
  }

  async Task<Peer> GetPeer(Token token)
  {
    while (true)
    {
      try
      {
        //string iP = GetIPAddress();

        string iP = "83.229.86.158"; // 84.74.69.100

        ISocketCommunication socketCommunication = Communication.GetSocketCommunication(Token, iP);

        Peer peer = new(this, socketCommunication, ConnectionType.OUTBOUND);

        await peer.Start();

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
      foreach (string dnsSeed in Token.GetSeedAddresses())
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

  internal Dictionary<string, MessageNetworkProtocol> CreateStateMachineProtocol()
  {
    Dictionary<string, MessageNetworkProtocol> protocol = new();

    Block blockDownload = new(Token);
    Block blockUpload = new(Token);

    AddMessageNetworkProtocol(protocol, new GetDataMessage(blockUpload));
    AddMessageNetworkProtocol(protocol, new GetHeadersMessage());
    AddMessageNetworkProtocol(protocol, new HeadersMessage());
    AddMessageNetworkProtocol(protocol, new BlockMessage(blockDownload));
    AddMessageNetworkProtocol(protocol, new TXMessage());
    AddMessageNetworkProtocol(protocol, new VerAckMessage());
    AddMessageNetworkProtocol(protocol, new VersionMessage());

    return protocol;
  }

  static void AddMessageNetworkProtocol(
    Dictionary<string, MessageNetworkProtocol> protocol,
    MessageNetworkProtocol message)
  {
    protocol.Add(message.GetCommand(), message);
  }

  internal async Task StartHeaderSync(Peer peer)
  {
    try
    {
      await LockBlockchain();

      if (NetworkParent.BlockchainRoot.HeaderTip.Height > BlockchainRoot.HeaderTip.Height)
        GetHeadersMessage.SendGetHeaders(peer, GetLocator());
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }


  // Das darf keine exception werfen.
  internal void NotifyChildNetworksIfAnchorToken(Block block)
  {
    Dictionary<byte[], TXOutputTokenAnchor> cacheAnchorTokens =
      new(new EqualityComparerByteArray());

    foreach (TX tX in block.TXs)
      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput is TXOutputTokenAnchor tokenAnchor)
          if (cacheAnchorTokens.TryAdd(tokenAnchor.HashBlockReferenced, tokenAnchor))
            NetworksChild.Find(n => n.Token.IDToken.IsAllBytesEqual(tokenAnchor.IDToken))
              ?.OnTokenAnchorParent(tokenAnchor);
  }

  async Task StartPeerConnectorInbound()
  {
    Communication.StartListenerCommunicationInbound(Token.Port);

    while (true)
    {
      ISocketCommunication socketCommunication = null;

      try
      {
        socketCommunication = await Communication.AcceptSocketCommunicationInbound();

        if (Peers.Any(p => p.GetIP().Equals(socketCommunication.GetIP()))
          || Peers.Count(p => p.Connection == ConnectionType.INBOUND) + 1 > COUNT_MAX_INBOUND_CONNECTIONS)
        {
          throw new ProtocolException("Inbound request rejected.");
        }

        await StartPeer(socketCommunication, ConnectionType.INBOUND);
      }
      catch
      {
        socketCommunication?.Dispose();

        await Task.Delay(30_000).ConfigureAwait(false);
      }
    }
  }

  async Task StartPeer(ISocketCommunication socketCommunication, ConnectionType connection)
  {
    Peer peer = new(this, socketCommunication, connection);

    await peer.Start();

    lock (LOCK_Peers)
      Peers.Add(peer);
  }
}