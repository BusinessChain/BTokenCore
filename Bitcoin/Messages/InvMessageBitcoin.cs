namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class InvMessageBitcoin : InvMessage
  {
    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

      for (int i = 0; i < inventoryCount; i++)
        if (Inventory.Parse(Payload, ref startIndex).Type == Inventory.InventoryType.MSG_BLOCK)
        {
          await GetHeadersMessage.SendGetHeaders(peer, await peer.Network.Blockchain.RunLocked(peer.Network.Blockchain.GetLocator));
          return;
        }
    }
  }
}
