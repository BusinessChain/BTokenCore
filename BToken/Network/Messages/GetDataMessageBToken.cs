namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class GetDataMessageBToken : GetDataMessage
  {
    HashSet<byte[]> HashesAccountsServed = new(new EqualityComparerByteArray());
    HashSet<byte[]> HashesBlocksCompactServed = new(new EqualityComparerByteArray());


    protected override async Task ServeInventoryToken(Peer peer, Inventory inventory)
    {
      DOSMonitor.Increment(1);

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;
      bool isSegment = inventory.Type == Inventory.InventoryType.MSG_SEGMENT_SWEEP;

      byte[] payload = await blockchainBToken.RunLocked(() => isSegment
        ? blockchainBToken.GetSegmentSweptByBlock(inventory.Hash)
        : blockchainBToken.GetBlocksCompactSerialized(inventory.Hash, BlocksCompactMessage.COUNT_MAX_BLOCKS_COMPACT, BlocksCompactMessage.SIZE_BUFFER_PAYLOAD));

      if (payload == null)
      {
        await SendInventories(peer, NotFoundMessage.Command, [inventory]);
        return;
      }

      if ((isSegment ? HashesAccountsServed : HashesBlocksCompactServed).Add(inventory.Hash))
        DOSMonitor.Decrement(1);

      await peer.SocketCommunication.SendMessage(isSegment ? AccountsMessage.CommandSegment : BlocksCompactMessage.Command, payload.Length, payload);
    }
  }
}
