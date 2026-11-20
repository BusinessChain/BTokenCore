namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  BlockchainBToken BlockchainBToken;


  internal NetworkBToken(
    BlockchainBToken blockchain,
    ICommunication communication,
    int port,
    string[] seedAddresses)
    : base(
      blockchain,
      communication,
      new Configuration()
      {
        Port = port,
        MagicBytes = [0xB7, 0xD0, 0xC3, 0xE5],
        ProtocolVersion = 1,
        SeedAddresses = seedAddresses,
        NetworkServicesLocal = Configuration.NODE_NETWORK,
        NetworkServicesRemote = Configuration.NODE_NETWORK,
        CountMaxInboundConnections = 32,
        EnableRelay = true
      })
  {
    BlockchainBToken = blockchain;
  }

  internal void SendTX(byte[] tXRaw)
  {
    int index = 0;

    TX tX = BlockchainBToken.Parser.ParseTX(tXRaw, ref index, isCoinbase: false, verifySignatures: true);

    if (!BlockchainBToken.IsTXInPool(tX.Hash))
      BlockchainBToken.AddTX(tX);

    QueueTXForRelay(tX);
  }

  void QueueTXForRelay(TX tX)
  {
    lock (LOCK_Peers)
      foreach (PeerBToken peer in Peers)
      {
        lock (peer.HashesTXRequested)
          if (peer.HashesTXRequested.ContainsKey(tX.Hash))
            continue;

        lock (peer.HashesTXQueued)
          peer.HashesTXQueued.Add(tX.Hash);
      }
  }

  protected override Peer CreatePeer(
    ISocketCommunication socketCommunication,
    Peer.ConnectionType connection)
  {
    return new PeerBToken(this, socketCommunication, connection);
  }
}
