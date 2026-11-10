namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class NotFoundMessageBToken : NotFoundMessage
  {
    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);
      int startIndexInventories = startIndex;

      Inventory inventory = Inventory.Parse(Payload, ref startIndex);

      if (inventory.Type is Inventory.InventoryType.MSG_SEGMENT_HASHES or Inventory.InventoryType.MSG_SEGMENT)
      {
        if (peer.StateCurrent != Peer.StateProtocol.AccountsDownload)
          return;

        DOSMonitor.Decrement(1);

        if (inventory.Type == Inventory.InventoryType.MSG_SEGMENT)
          peer.HashesSegmentRefused.Add(inventory.Hash);
        else
        {
          ((PeerBToken)peer).HashBlockAccountsRefused = inventory.Hash;

          BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;
          await blockchainBToken.RunLocked(blockchainBToken.EndRequestHashesSegment);
        }

        peer.StateCurrent = Peer.StateProtocol.Idle;
        return;
      }

      if (inventory.Type == Inventory.InventoryType.MSG_TX)
      {
        startIndex = startIndexInventories;
        bool isRequestAnswered = false;

        Dictionary<byte[], DateTime> hashesTXRequested = ((PeerBToken)peer).HashesTXRequested;

        lock (hashesTXRequested)
          for (int i = 0; i < inventoryCount; i++)
            if (hashesTXRequested.Remove(Inventory.Parse(Payload, ref startIndex).Hash))
              isRequestAnswered = true;

        if (isRequestAnswered)
          DOSMonitor.Decrement(1);

        return;
      }

      await base.Run(peer);
    }
  }
}
