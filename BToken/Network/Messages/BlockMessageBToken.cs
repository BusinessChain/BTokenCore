namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class BlockMessageBToken : BlockMessage
  {
    HashSet<byte[]> HashesAccountsReceived = new(new EqualityComparerByteArray());
    HashSet<byte[]> HashesBlocksCompactReceived = new(new EqualityComparerByteArray());


    internal BlockMessageBToken(Blockchain blockchain)
      : base(blockchain)
    { }

    internal void RefundInventoryTokenReceived(Inventory inventory)
    {
      HashSet<byte[]> hashesReceived = inventory.Type == Inventory.InventoryType.MSG_SEGMENT_SWEEP
        ? HashesAccountsReceived
        : HashesBlocksCompactReceived;

      if (hashesReceived.Add(inventory.Hash))
        DOSMonitorRequests.Decrement(1);
    }

    protected override async Task<bool> TryRequestItemNext(Peer peer)
    {
      PeerBToken peerBToken = (PeerBToken)peer;
      BlockchainBToken blockchainBToken = peerBToken.BlockchainBToken;

      Inventory inventoryBlocksCompact = await blockchainBToken.RunLocked(() =>
        blockchainBToken.FetchHeaderBlockCompactNext(h => !peer.HashesRefused.Contains(h.Hash)) is Header headerTip
          ? new Inventory(Inventory.InventoryType.MSG_BLOCKS_COMPACT, headerTip.Hash)
          : null);

      if (inventoryBlocksCompact != null)
        return await RequestInventoryToken(peerBToken, inventoryBlocksCompact, Peer.StateProtocol.BlocksCompactDownload);

      if (await base.TryRequestItemNext(peer))
        return true;

      Inventory inventoryAccountsMissing = await blockchainBToken.RunLocked(() =>
        blockchainBToken.FetchHeaderSweepMissingNext(h => !peer.HashesRefused.Contains(h.Hash)) is Header headerSweep
          ? new Inventory(Inventory.InventoryType.MSG_SEGMENT_SWEEP, headerSweep.Hash)
          : null);

      if (inventoryAccountsMissing == null)
        return false;

      return await RequestInventoryToken(peerBToken, inventoryAccountsMissing, Peer.StateProtocol.AccountsDownload);
    }

    async Task<bool> RequestInventoryToken(PeerBToken peer, Inventory inventory, Peer.StateProtocol state)
    {
      peer.InventoryRequested = inventory;

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = state;

      await SendInventories(peer, GetDataMessage.Command, [inventory]);

      return true;
    }
  }
}
