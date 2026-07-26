using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using LiteDB;


namespace BTokenLib
{
  public abstract partial class Token
  {
    protected partial class NetworkToken
    {
      public NetworkToken NetworkParent;

      public List<NetworkToken> NetworksChild = new();

      public Token Token;

      LiteDatabase LiteDatabase;
      ILiteCollection<BsonDocument> DatabaseMetaCollection;
      ILiteCollection<BsonDocument> DatabaseHeaderCollection;


      public NetworkToken(
        Token tokenParent,
        Token token,
        int port,
        bool flagEnableInboundConnections,
        bool flagEnableRelay)
      {
        NetworkParent = tokenParent.Network; 
        Token = token;

        BlockchainRoot = new(Token, this);

        EnableInboundConnections = flagEnableInboundConnections;
        EnableRelay = flagEnableRelay;

        LogEntryNotifier = token.LogEntryNotifier;
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

        LiteDatabase = new LiteDatabase($"{token.GetName()}.db;Mode=Exclusive");
        DatabaseHeaderCollection = LiteDatabase.GetCollection<BsonDocument>("headers");
        DatabaseMetaCollection = LiteDatabase.GetCollection<BsonDocument>("meta");
      }

      public void Start()
      {
        if (NetworkParent != null)
          NetworkParent.Start();

        Log($"Load state from disk.");
        BlockchainRoot.LoadFromDisk();

        Log($"Start Network.");
        StartPeerConnector();
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
        messageLog.Log(this, LogEntryNotifier);
      }
    }
  }
}