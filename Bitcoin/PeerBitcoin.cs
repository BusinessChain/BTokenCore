namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class PeerBitcoin : Peer
  {
    BlockchainBitcoin BlockchainBitcoin;

    internal Header HeaderTipReceivedLast;

    byte[] HashTXAnchorAnnounced;


    internal PeerBitcoin(
      NetworkBitcoin network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      BlockchainBitcoin = network.BlockchainBitcoin;

      ProtocolStateMachine.Add(GetHeadersMessage.Command, new GetHeadersMessage(network.Blockchain));
      ProtocolStateMachine.Add(HeadersMessage.Command, new HeadersMessageBitcoin(network.Blockchain));
      ProtocolStateMachine.Add(InvMessage.Command, new InvMessageBitcoin(network.Blockchain));
      ProtocolStateMachine.Add(VerAckMessage.Command, new VerAckMessageBitcoin(network.Blockchain));
      ProtocolStateMachine.Add(NotFoundMessage.Command, new NotFoundMessage());
      ProtocolStateMachine.Add(GetDataMessage.Command, new GetDataMessage(network.Blockchain));
      ProtocolStateMachine.Add(BlockMessage.Command, new BlockMessage(network.Blockchain));
    }

    internal override bool CanDeliverBlock(Header header)
    {
      Header headerAncestor = HeaderTipReceivedLast;

      while (headerAncestor != null && headerAncestor.Height > header.Height)
        headerAncestor = headerAncestor.HeaderPrevious;

      return base.CanDeliverBlock(header) && headerAncestor == header;
    }

    protected override async Task RelayTXs()
    {
      byte[] hashTXAnchorOpen = await BlockchainBitcoin.RunLocked(BlockchainBitcoin.GetHashTXAnchorOpen);

      if (hashTXAnchorOpen == null
        || HashTXAnchorAnnounced != null && hashTXAnchorOpen.IsAllBytesEqual(HashTXAnchorAnnounced))
        return;

      await NetworkMessage.SendInventories(
        this,
        InvMessage.Command,
        [new(Inventory.InventoryType.MSG_TX, hashTXAnchorOpen)]);

      HashTXAnchorAnnounced = hashTXAnchorOpen;
    }
  }
}
