namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class PeerBitcoin : Peer
  {
    internal PeerBitcoin(
      NetworkBitcoin network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      ProtocolStateMachine.Add(GetHeadersMessage.Command, new GetHeadersMessage(network.Blockchain));
    }

    internal override bool CanDeliverBlock(Header header)
    {
      Header headerAncestor = HeaderTipReceivedLast;

      while (headerAncestor != null && headerAncestor.Height > header.Height)
        headerAncestor = headerAncestor.HeaderPrevious;

      return base.CanDeliverBlock(header) && headerAncestor == header;
    }
  }
}
