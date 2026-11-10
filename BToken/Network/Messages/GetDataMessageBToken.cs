namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class GetDataMessageBToken : GetDataMessage
  {
    HashSet<byte[]> HashesAccountsServed = new(new EqualityComparerByteArray());


    protected override async Task ServeAccounts(Peer peer, Inventory inventory)
    {
      DOSMonitor.Increment(1);

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;
      bool isHashesSegment = inventory.Type == Inventory.InventoryType.MSG_SEGMENT_HASHES;

      byte[] payload = await blockchainBToken.RunLocked(() => isHashesSegment
        ? blockchainBToken.GetHashesSegmentAtBlock(inventory.Hash)
        : blockchainBToken.GetSegmentServed(inventory.Hash));

      if (payload == null)
      {
        await SendInventories(peer, NotFoundMessage.Command, [inventory]);
        return;
      }

      if (RefundAccountsServed(HashesAccountsServed, inventory))
        DOSMonitor.Decrement(1);

      await peer.SocketCommunication.SendMessage(
        isHashesSegment ? AccountsMessage.CommandHashesSegment : AccountsMessage.CommandSegment,
        payload.Length,
        payload);
    }

    internal static bool RefundAccountsServed(HashSet<byte[]> hashesAccountsServed, Inventory inventory)
    {
      if (inventory.Type == Inventory.InventoryType.MSG_SEGMENT_HASHES && !hashesAccountsServed.Contains(inventory.Hash))
        hashesAccountsServed.Clear();

      return hashesAccountsServed.Add(inventory.Hash);
    }
  }
}
