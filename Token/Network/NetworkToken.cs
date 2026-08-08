using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

using LiteDB;


namespace BTokenCore;

public partial class NetworkToken
{
  public NetworkToken NetworkParent;

  public List<NetworkToken> NetworksChild = new();

  public Token Token;

  LiteDatabase LiteDatabase;
  ILiteCollection<BsonDocument> DatabaseMetaCollection;
  ILiteCollection<BsonDocument> DatabaseHeaderCollection;

  const int COUNT_MAX_OUTBOUND_CONNECTIONS = 3;
  const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 5;
  const int COUNT_MAX_INBOUND_CONNECTIONS = 8;

  bool EnableInboundConnections;
  bool EnableRelay;

  object LOCK_Peers = new();
  List<Peer> Peers = new();

  public enum ConnectionType { OUTBOUND, INBOUND };
  List<string> IPAddresses = new();


  public NetworkToken(
    Token tokenParent,
    Token token,
    int port,
    bool flagEnableInboundConnections,
    bool flagEnableRelay)
  {
    NetworkParent = tokenParent?.Network;
    Token = token;

    BlockchainRoot = new(Token, this);

    EnableInboundConnections = flagEnableInboundConnections;
    EnableRelay = flagEnableRelay;

    LiteDatabase = new LiteDatabase($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    DatabaseHeaderCollection = LiteDatabase.GetCollection<BsonDocument>("headers");
    DatabaseMetaCollection = LiteDatabase.GetCollection<BsonDocument>("meta");
  }

  public void Start()
  {
    if (NetworkParent != null)
      NetworkParent.Start();

    BlockchainRoot.LoadFromDisk();

    StartPeerConnectorOutbound();

    if (EnableInboundConnections)
      StartPeerConnectorInbound();
  }

  async Task StartPeerConnectorOutbound()
  {
    while (true)
    {
      Peers.RemoveAll(p => p.IsDisposed());

      while (Peers.Count < COUNT_MAX_OUTBOUND_CONNECTIONS)
        Peers.Add(await GetInterfacePeer(Token));

      await Task.Delay(1000 * TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS).ConfigureAwait(false);
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

  async Task<Peer> GetInterfacePeer(Token token)
  {
    while (true)
    {
      try
      {
        //string iP = GetIPAddress();

        string iP = "83.229.86.158";
        // 84.74.69.100

        ISocketCommunication socketCommunication = token.GetSocketCommunication(iP);

        await StartPeer(socketCommunication, ConnectionType.OUTBOUND);
      }
      catch
      {
        await Task.Delay(1000);
      }
    }
  }

  Dictionary<string, MessageNetworkProtocol> CreateStateMachineProtocol()
  {
    Dictionary<string, MessageNetworkProtocol> protocol = new();

    Block blockDownload = new(Token);
    Block blockUpload = new(Token);

    AddMessageNetworkProtocol(protocol, new GetDataMessage(blockUpload));
    AddMessageNetworkProtocol(protocol, new GetHeadersMessage());
    AddMessageNetworkProtocol(protocol, new HeadersMessage(blockDownload));
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

  async Task StartHeaderSync(Peer peer)
  {
    if (!await TryLockBlockchain(10000))
      return;

    try
    {
      if (NetworkParent.BlockchainRoot.GetHeight() > BlockchainRoot.GetHeight())
        GetHeadersMessage.SendGetHeaders(peer, GetLocator());
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }

  void NotifyChildNetworksOfAnchorToken(Block block)
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
    Token.StartListenerCommunicationInbound();

    while (true)
    {
      ISocketCommunication socketCommunicationInbound = null;

      try
      {
        socketCommunicationInbound = await Token.AcceptSocketCommunicationInbound();

        if (Peers.Any(p => p.GetIP().Equals(socketCommunicationInbound.GetIP()))
          || Peers.Count(p => p.Connection == ConnectionType.INBOUND) + 1 > COUNT_MAX_INBOUND_CONNECTIONS)
        {
          throw new ProtocolException("Inbound request rejected.");
        }

        await StartPeer(socketCommunicationInbound, ConnectionType.INBOUND);
      }
      catch
      {
        socketCommunicationInbound?.Dispose();

        await Task.Delay(30_000).ConfigureAwait(false);
      }
    }
  }

  async Task StartPeer(ISocketCommunication socketCommunication, ConnectionType connection)
  {
    Peer peer = new(CreateStateMachineProtocol(), socketCommunication, connection);

    await peer.Start();

    lock (LOCK_Peers)
      Peers.Add(peer);
  }
}