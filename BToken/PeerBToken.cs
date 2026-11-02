namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class PeerBToken : Peer
  {
    internal PeerBToken(
      NetworkBToken network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      ProtocolStateMachine.Add(DBMessage.Command, new DBMessage(network.Blockchain));
      ProtocolStateMachine.Add(TXMessage.Command, new TXMessage(network.Blockchain, network));
    }

    internal override bool CanDeliverBlock(Header header)
    {
      bool isAnnouncedByPeer = HashBlockAnnounced != null && header.Hash.IsAllBytesEqual(HashBlockAnnounced);
      bool hasBlockHadTimeToSpread = !header.IsParentNewest();

      return base.CanDeliverBlock(header) && (hasBlockHadTimeToSpread || isAnnouncedByPeer);
    }
  }
}
