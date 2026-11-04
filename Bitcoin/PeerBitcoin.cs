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
      MessagesByCommand.Add(GetHeadersMessage.Command, new GetHeadersMessage(network.Blockchain));
      MessagesByCommand.Add(HeadersMessage.Command, new HeadersMessageBitcoin(network.Blockchain));
      MessagesByCommand.Add(InvMessage.Command, new InvMessageBitcoin(network.Blockchain));
      MessagesByCommand.Add(VerAckMessage.Command, new VerAckMessageBitcoin(network.Blockchain));
      MessagesByCommand.Add(NotFoundMessage.Command, new NotFoundMessage());
      MessagesByCommand.Add(GetDataMessage.Command, new GetDataMessage(network.Blockchain));
      MessagesByCommand.Add(BlockMessage.Command, new BlockMessage(network.Blockchain));
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
