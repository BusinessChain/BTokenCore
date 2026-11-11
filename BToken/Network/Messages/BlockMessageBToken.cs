namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class BlockMessageBToken : BlockMessage
  {
    HashSet<byte[]> HashesAccountsReceived = new(new EqualityComparerByteArray());


    internal BlockMessageBToken(Blockchain blockchain)
      : base(blockchain)
    { }

    internal void RefundAccountsReceived(Inventory inventory)
    {
      if (HashesAccountsReceived.Add(inventory.Hash))
        DOSMonitorRequests.Decrement(1);
    }

    protected override async Task<bool> TryRequestItemNext(Peer peer)
    {
      if (await base.TryRequestItemNext(peer))
        return true;

      PeerBToken peerBToken = (PeerBToken)peer;
      BlockchainBToken blockchainBToken = peerBToken.BlockchainBToken;

      Inventory inventoryAccountsMissing = await blockchainBToken.RunLocked(() =>
        blockchainBToken.FetchHeaderSweepMissingNext(h => !peer.HashesSegmentRefused.Contains(h.Hash)) is Header headerSweep
          ? new Inventory(Inventory.InventoryType.MSG_SEGMENT_SWEEP, headerSweep.Hash)
          : null);

      if (inventoryAccountsMissing == null)
        return false;

      peerBToken.InventoryAccountsRequested = inventoryAccountsMissing;

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.AccountsDownload;

      await SendInventories(peer, GetDataMessage.Command, [inventoryAccountsMissing]);

      return true;
    }
  }
}
