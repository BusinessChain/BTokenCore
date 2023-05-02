using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace BTokenLib
{
  public partial class Network
  {
    Token Token;
    Blockchain Blockchain;

    const int TIMEOUT_RESPONSE_MILLISECONDS = 5000;
    const int TIMESPAN_PEER_BANNED_SECONDS = 120;

    StreamWriter LogFile;

    UInt16 Port;

    public bool EnableInboundConnections;

    object LOCK_Peers = new();
    List<Peer> Peers = new();

    DirectoryInfo DirectoryPeers;
    DirectoryInfo DirectoryPeersActive;
    DirectoryInfo DirectoryPeersDisposed;
    DirectoryInfo DirectoryPeersArchive;


    public Network(
      Token token,
      bool flagEnableInboundConnections)
    {
      Token = token;
      Blockchain = token.Blockchain;

      Port = token.Port;
      EnableInboundConnections = flagEnableInboundConnections;

      string pathRoot = token.GetName();

      LogFile = new StreamWriter(
        Path.Combine(pathRoot, "LogNetwork"),
        false);

      DirectoryPeers = Directory.CreateDirectory(
        Path.Combine(pathRoot, "logPeers"));

      DirectoryPeersActive = Directory.CreateDirectory(
        Path.Combine(
          DirectoryPeers.FullName,
          "active"));

      DirectoryPeersDisposed = Directory.CreateDirectory(
        Path.Combine(
          DirectoryPeers.FullName,
          "disposed"));

      DirectoryPeersArchive = Directory.CreateDirectory(
        Path.Combine(DirectoryPeers.FullName, "archive"));

      LoadNetworkConfiguration(pathRoot);

      foreach (FileInfo file in DirectoryPeersActive.GetFiles())
        file.MoveTo(Path.Combine(DirectoryPeersArchive.FullName, file.Name));

    }

    public void Start()
    {
      $"Start Network {Token.GetName()}".Log(this, LogFile);

      StartPeerConnector();

      StartSynchronizerLoop();

      if (EnableInboundConnections)
        StartPeerInboundListener();
    }

    void LoadNetworkConfiguration(string pathConfigFile)
    {
      $"Load Network configuration {pathConfigFile}.".Log(this, LogFile);
    }


    public void AdvertizeBlockToNetwork(Block block)
    {
      AdvertizeBlockToNetwork(block, null);
    }

    void AdvertizeBlockToNetwork(Block block, Peer peerSource)
    {
      Peers.ForEach(p =>
      {
        if (p != peerSource && p.IsStateIdle() &&
        (p.HeaderUnsolicited == null ||
        !p.HeaderUnsolicited.Hash.IsEqual(block.Header.Hash)))
        {
          p.AdvertizeBlock(block);
        }
      });
    }

    bool TryGetPeerIdle(out Peer peer)
    {
      lock (LOCK_Peers)
        peer = Peers.Find(p => p.IsStateIdle());

      return peer != null;
    }

    public void AdvertizeTX(TX tX)
    {
      //$"Advertize rawTX {tX.GetStringTXRaw()} to {this}."
      //  .Log(this, LogFile);

      // should Lock Blockchain

      List<Peer> peersAdvertized = new();

      while (TryGetPeerIdle(out Peer peer))
        peersAdvertized.Add(peer);

      peersAdvertized.Select(p => p.AdvertizeTX(tX)).ToArray();
    }

    public void RemovePeer(string iPAddress)
    {
      lock (LOCK_Peers)
      {
        Peer peerRemove = Peers.
          Find(p => p.ToString() == iPAddress);

        if (peerRemove != null)
        {
          peerRemove.SetStateDisposed("Manually removed peer.");
        }
      }
    }

    public string GetStatus()
    {
      string statusPeers = "";
      int countPeers;

      lock (LOCK_Peers)
      {
        Peers.ForEach(p => { statusPeers += p.GetStatus(); });
        countPeers = Peers.Count;
      }

      return
        "\n\n Status Network: \n" +
        statusPeers +
        $"\n\n Count peers: {countPeers} \n";
    }

    public override string ToString()
    {
      return Token.GetType().Name + "." + GetType().Name;
    }
  }
}
