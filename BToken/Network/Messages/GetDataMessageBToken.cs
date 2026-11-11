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

      byte[] payload = await blockchainBToken.RunLocked(() => blockchainBToken.GetSegmentAtSweep(inventory.Hash));

      if (payload == null)
      {
        await SendInventories(peer, NotFoundMessage.Command, [inventory]);
        return;
      }

      if (HashesAccountsServed.Add(inventory.Hash))
        DOSMonitor.Decrement(1);

      await peer.SocketCommunication.SendMessage(AccountsMessage.CommandSegment, payload.Length, payload);
    }
  }
}
