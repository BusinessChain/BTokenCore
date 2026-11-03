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
        MagicBytes = [0xF9, 0xBE, 0xB4, 0xD9],
        ProtocolVersion = 70015,
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
  {
    blockchain.OnTXAnchorCreated += Broadcast;
  }

  void Broadcast(TX tX)
  {
    lock (LOCK_Peers)
      foreach (Peer peer in Peers)
        NetworkMessage.SendInventories(peer, InvMessage.Command, [new(Inventory.InventoryType.MSG_TX, tX.Hash)]);
  }

  protected override Peer CreatePeer(
    ISocketCommunication socketCommunication,
    Peer.ConnectionType connection)
  {
    return new PeerBitcoin(this, socketCommunication, connection);
  }
}
