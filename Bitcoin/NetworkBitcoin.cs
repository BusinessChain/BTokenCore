namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  internal NetworkBitcoin(
    BlockchainBitcoin blockchain,
    ICommunication communication)
    : base(
      blockchain,
      communication,
      new Configuration()
      {
        Port = 8333,
        IsProtocolBitcoin = true,
        NetworkServicesRemote = Configuration.NODE_NETWORK,
        SeedAddresses =
          [
            "seed.bitcoin.sipa.be",
            "dnsseed.bluematt.me",
            "dnsseed.bitcoin.dashjr.org",
            "seed.bitcoinstats.com",
            "seed.bitnodes.io"
          ]
      })
  { }

  protected override Peer CreatePeer(
    ISocketCommunication socketCommunication,
    Peer.ConnectionType connection)
  {
    return new PeerBitcoin(this, socketCommunication, connection);
  }
}
