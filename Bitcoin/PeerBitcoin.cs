namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class PeerBitcoin : Peer
  {
    internal Header HeaderTipReceivedLast;


    internal PeerBitcoin(
      NetworkBitcoin network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      ProtocolStateMachine.Add(GetHeadersMessage.Command, new GetHeadersMessage(network.Blockchain));
      ProtocolStateMachine.Add(HeadersMessage.Command, new HeadersMessageBitcoin(network.Blockchain));
      ProtocolStateMachine.Add(InvMessage.Command, new InvMessageBitcoin(network.Blockchain));
      ProtocolStateMachine.Add(VerAckMessage.Command, new VerAckMessageBitcoin(network.Blockchain));
      ProtocolStateMachine.Add(NotFoundMessage.Command, new NotFoundMessage());
      ProtocolStateMachine.Add(GetDataMessage.Command, new GetDataMessage(network.Blockchain));
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
