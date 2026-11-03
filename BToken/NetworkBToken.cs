namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  BlockchainBToken BlockchainBToken;


  internal NetworkBToken(
    BlockchainBToken blockchain,
    ICommunication communication)
    : base(
      blockchain,
      communication,
      new Configuration()
      {
        Port = 8777,
        MagicBytes = [0xB7, 0xD0, 0xC3, 0xE5],
        ProtocolVersion = 1,
        SeedAddresses = ["83.229.86.158"],
        NetworkServicesLocal = Configuration.NODE_NETWORK,
        NetworkServicesRemote = Configuration.NODE_NETWORK,
        CountMaxInboundConnections = 32,
        EnableRelay = true
      })
  {
    BlockchainBToken = blockchain;
  }

  internal async Task SubmitTX(byte[] tXRaw)
  {
    int index = 0;

    TX tX = BlockchainBToken.ParseTX(tXRaw, ref index, flagIsCoinbase: false);

    await AddTX(tX);
  }

  async Task AddTX(TX tX)
  {
    await BlockchainBToken.RunLocked(() => BlockchainBToken.AddTX(tX));

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
