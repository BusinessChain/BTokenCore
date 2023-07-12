﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace BTokenLib
{
  public partial class Network
  {
    Token Token;

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

      if (Token.TokenParent == null)
        StartSynchronizerLoop();

      if (EnableInboundConnections)
        StartPeerInboundConnector();
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
      lock (LOCK_Peers)
        foreach (Peer peer in Peers)
          peer.TryAdvertizeTX(tX);
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
        "\n Status Network: \n" +
        statusPeers +
        $"Count peers: {countPeers} \n";
    }

    public override string ToString()
    {
      return Token.GetType().Name + "." + GetType().Name;
    }
  }
}
