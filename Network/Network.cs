using LiteDB;


namespace BTokenCore;

internal partial class Network
{
  internal Token Token;

  internal PeerConnector PeerConnector;

  internal LiteDatabase LiteDatabase;
  internal ILiteCollection<BsonDocument> DatabaseHeaderCollection;
  internal ILiteCollection<BsonDocument> DatabaseBlockCollection;


  internal Network(
    ICommunication communication,
    Token token,
    Header headerRootParent,
    SemaphoreSlim semaphoreBlockchain,
    bool flagEnableInboundConnections,
    bool flagEnableRelay)
  {
    Token = token;
    SemaphoreBlockchain = semaphoreBlockchain;

    BlockchainRoot = new(Token.CreateHeaderGenesis());
    BlockchainRoot.HeaderRoot.HeaderParent = headerRootParent;

    PeerConnector = new(
      this,
      communication,
      token,
      flagEnableInboundConnections,
      flagEnableRelay);

    LiteDatabase = new LiteDatabase($"Filename={token.GetName() + "Network"}.db;Mode=Exclusive");
    DatabaseHeaderCollection = LiteDatabase.GetCollection<BsonDocument>("headers");
    DatabaseBlockCollection = LiteDatabase.GetCollection<BsonDocument>("blocks");
  }

  internal void Start()
  {
    LoadBlockchain();

    PeerConnector.Start();
  }
}
