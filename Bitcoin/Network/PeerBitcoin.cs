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
      MessagesByCommand.Add(GetHeadersMessage.Command, new GetHeadersMessage());
      MessagesByCommand.Add(HeadersMessage.Command, new HeadersMessageBitcoin());
      MessagesByCommand.Add(InvMessage.Command, new InvMessageBitcoin());
      MessagesByCommand.Add(VerAckMessage.Command, new VerAckMessageBitcoin());
      MessagesByCommand.Add(NotFoundMessage.Command, new NotFoundMessage());
      MessagesByCommand.Add(GetDataMessage.Command, new GetDataMessage());
      MessagesByCommand.Add(BlockMessage.Command, new BlockMessage(network.Blockchain));
    }

    internal override bool CanDeliverBlock(Header header)
    {
      return base.CanDeliverBlock(header) && HeaderTipReceivedLast?.GetHeaderAncestor(header.Height) == header;
    }
  }
}
