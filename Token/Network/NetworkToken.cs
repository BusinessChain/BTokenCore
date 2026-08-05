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

public partial class NetworkToken : ISocketToken
{
  public NetworkToken NetworkParent;

  public List<NetworkToken> NetworksChild = new();

  public Token Token;

  LiteDatabase LiteDatabase;
  ILiteCollection<BsonDocument> DatabaseMetaCollection;
  ILiteCollection<BsonDocument> DatabaseHeaderCollection;

  const int COUNT_MAX_OUTBOUND_CONNECTIONS = 3;
  const int TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS = 10;

  bool EnableInboundConnections;
  bool EnableRelay;

  object LOCK_Peers = new();
  List<IPeer> Peers = new();

  DirectoryInfo DirectoryPeers;
  DirectoryInfo DirectoryPeersActive;
  DirectoryInfo DirectoryPeersArchive;
  DirectoryInfo DirectoryPeersDisposed;


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

    string pathRoot = token.GetName();

    DirectoryPeers = Directory.CreateDirectory(
      Path.Combine(pathRoot, "logPeers"));

    DirectoryPeersActive = Directory.CreateDirectory(
      Path.Combine(DirectoryPeers.FullName, "active"));

    DirectoryPeersDisposed = Directory.CreateDirectory(
      Path.Combine(DirectoryPeers.FullName, "disposed"));

    DirectoryPeersArchive = Directory.CreateDirectory(
      Path.Combine(DirectoryPeers.FullName, "archive"));

    foreach (FileInfo file in DirectoryPeersActive.GetFiles())
      file.MoveTo(Path.Combine(DirectoryPeersArchive.FullName, file.Name));

    string connectionString = $"Filename={token.GetName() + "Network"}.db;Mode=Exclusive";
    LiteDatabase = new LiteDatabase(connectionString);
    DatabaseHeaderCollection = LiteDatabase.GetCollection<BsonDocument>("headers");
    DatabaseMetaCollection = LiteDatabase.GetCollection<BsonDocument>("meta");
  }

  public void Start()
  {
    if (NetworkParent != null)
      NetworkParent.Start();

    BlockchainRoot.LoadFromDisk();

    StartPeerConnector();
  }

  async Task StartPeerConnector()
  {
    if (EnableInboundConnections)
      SocketToken.StartPeerInboundConnector();

    while (true)
    {
      Peers.RemoveAll(p => p.IsDisposed());

      while (Peers.Count < COUNT_MAX_OUTBOUND_CONNECTIONS)
        Peers.Add(await GetInterfacePeer(Token));

      await Task.Delay(1000 * TIMESPAN_LOOP_PEER_CONNECTOR_SECONDS).ConfigureAwait(false);
    }
  }

  List<string> IPAddresses = new();

  public async Task<IPeer> GetInterfacePeer(Token token)
  {
    IPAddress iP = LoadIPAddress();

    TcpClient tcpClient = new TcpClient();

    ISocketCommunication socketCommunication =
      await token.SocketCommunication.GetSocketCommunication();

    try
    {
      Peer peer = new(
        CreateStateMachineProtocol(),
        socketCommunication,
        Peer.ConnectionType.OUTBOUND,
        iP);

      await peer.Start();

      return peer;
    }
    catch (Exception ex)
    {
      tcpClient.Dispose();
      return null;
    }
  }

  IPAddress LoadIPAddress()
  {
    if (IPAddresses.Count == 0)
    {
      IPAddresses = GetSeedAddresses();

      foreach (FileInfo iPDisposed in DirectoryPeersDisposed.EnumerateFiles())
      {
        if (iPDisposed.Name.Contains(ConnectionType.OUTBOUND.ToString()))
        {
          int secondsBanned = TIMESPAN_PEER_BANNED_SECONDS -
            (int)(DateTime.Now - iPDisposed.CreationTime).TotalSeconds;

          if (0 < secondsBanned)
          {
            IPAddresses.RemoveAll(iP => iPDisposed.Name.Contains(iP));
            continue;
          }

          iPDisposed.MoveTo(Path.Combine(
            DirectoryPeersArchive.FullName,
            iPDisposed.Name));
        }
      }

      foreach (FileInfo fileIPAddressArchive in DirectoryPeersArchive.EnumerateFiles())
      {
        string iPFromFile = fileIPAddressArchive.Name.GetIPFromFileName();

        if (!IPAddresses.Any(ip => ip == iPFromFile))
          IPAddresses.Add(iPFromFile);
      }

      foreach (FileInfo fileIPAddressActive in DirectoryPeersActive.EnumerateFiles())
        IPAddresses.RemoveAll(iP => fileIPAddressActive.Name.GetIPFromFileName() == iP);
    }

    while (iPAddresses.Count < maxCount && IPAddresses.Count > 0)
    {
      int randomIndex = randomGenerator.Next(IPAddresses.Count);

      string iPAddress = IPAddresses[randomIndex];
      IPAddresses.RemoveAt(randomIndex);

      if (!Peers.Any(p => p.IPAddress.ToString() == iPAddress))
        iPAddresses.Add(iPAddress);
    }

    return iPAddresses.Select(iP => IPAddress.Parse(iP)).ToList();
  }

  public List<string> GetSeedAddresses()
  {
    //mit DNS seeds arbeiten.
    //seed.bitcoin.sipa.be
    //dnsseed.bluematt.me
    //dnsseed.bitcoin.dashjr.org
    //seed.bitcoinstats.com
    //seed.bitnodes.io

    return new List<string>()
        {"83.229.86.158" 
        // 84.74.69.100
        };
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

  void Log(string messageLog)
  {
    messageLog.Log(this, SocketToken);
  }
}