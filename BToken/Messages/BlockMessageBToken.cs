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
      if (GetDataMessageBToken.RefundAccountsServed(HashesAccountsReceived, inventory))
        DOSMonitorRequests.Decrement(1);
    }

    protected override async Task<bool> TryRequestItemNext(Peer peer)
    {
      if (await base.TryRequestItemNext(peer))
        return true;

      PeerBToken peerBToken = (PeerBToken)peer;
      BlockchainBToken blockchainBToken = peerBToken.BlockchainBToken;

      Inventory inventoryAccountsMissing = await blockchainBToken.RunLocked(() =>
        blockchainBToken.FetchHashSegmentMissingNext(h => !peer.HashesSegmentRefused.Contains(h), peerBToken.HashBlockAccountsRefused) is byte[] hashSegment
          ? new Inventory(Inventory.InventoryType.MSG_SEGMENT, hashSegment)
          : blockchainBToken.FetchHashBlockAccountsMissing(
              h => ((NetworkBToken)peer.Network).IsPeerPickedForHashesSegment(peerBToken, h)) is byte[] hashBlock
            ? new Inventory(Inventory.InventoryType.MSG_SEGMENT_HASHES, hashBlock)
            : null);

      if (inventoryAccountsMissing == null)
        return false;

      if (inventoryAccountsMissing.Type == Inventory.InventoryType.MSG_SEGMENT_HASHES)
        peerBToken.HashBlockAccountsAsked = inventoryAccountsMissing.Hash;

      peerBToken.InventoryAccountsRequested = inventoryAccountsMissing;

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.AccountsDownload;

      await SendInventories(peer, GetDataMessage.Command, [inventoryAccountsMissing]);

      return true;
    }
  }
}
