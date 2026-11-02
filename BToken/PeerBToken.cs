namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class PeerBToken : Peer
  {
    internal byte[] HashBlockAnnounced;

    internal Dictionary<byte[], DateTime> HashesTXRequested = new(new EqualityComparerByteArray());

    internal HashSet<byte[]> HashesTXQueued = new(new EqualityComparerByteArray());
    internal DateTime TimeInvNext;
    internal DOSMonitor ThrottleTXAnnouncedToPeer = new(
      TXMessage.MAX_LEVEL_ANNOUNCED,
      TXMessage.AMOUNT_DRAIN_DOS_PER_10_MINUTES);


    internal PeerBToken(
      NetworkBToken network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      ProtocolStateMachine.Add(DBMessage.Command, new DBMessage(network.Blockchain));
      ProtocolStateMachine.Add(TXMessage.Command, new TXMessageBToken(network));
      ProtocolStateMachine.Add(HeadersMessage.Command, new HeadersMessageBToken(network.Blockchain));
      ProtocolStateMachine.Add(InvMessage.Command, new InvMessageBToken(network.Blockchain));
      ProtocolStateMachine.Add(VerAckMessage.Command, new VerAckMessage());
      ProtocolStateMachine.Add(NotFoundMessage.Command, new NotFoundMessageBToken());
    }

    internal override bool CanDeliverBlock(Header header)
    {
      bool isAnnouncedByPeer = HashBlockAnnounced != null && header.Hash.IsAllBytesEqual(HashBlockAnnounced);
      bool hasBlockHadTimeToSpread = !header.IsParentNewest();

      return base.CanDeliverBlock(header) && (hasBlockHadTimeToSpread || isAnnouncedByPeer);
    }
  }
}
