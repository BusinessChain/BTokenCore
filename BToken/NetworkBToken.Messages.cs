namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class HeadersMessageBToken : HeadersMessage
  {
    internal HeadersMessageBToken(Blockchain blockchain)
      : base(blockchain)
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;
      int countHeaders = VarInt.GetInt(Payload, ref startIndex);

      if (countHeaders == 1)
        ((PeerBToken)peer).HashBlockAnnounced = Blockchain.ParseHeader(Payload, ref startIndex).Hash;
    }
  }

  class InvMessageBToken : InvMessage
  {
    internal InvMessageBToken(Blockchain blockchain)
      : base(blockchain)
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

      List<byte[]> hashesTX = new();

      for (int i = 0; i < inventoryCount; i++)
      {
        Inventory inventory = Inventory.Parse(Payload, ref startIndex);

        if (inventory.Type == Inventory.InventoryType.MSG_TX)
          hashesTX.Add(inventory.Hash);
      }

      List<byte[]> hashesTXNotInPool = await Blockchain.GetHashesTXNotInPool(hashesTX);

      lock (peer.HashesTXRequested)
        foreach (byte[] hashTX in hashesTXNotInPool)
          peer.HashesTXRequested.TryAdd(hashTX, DateTime.UtcNow);

      if (hashesTXNotInPool.Count > 0)
        await SendInventories(
          peer,
          GetDataMessage.Command,
          hashesTXNotInPool.Select(h => new Inventory(Inventory.InventoryType.MSG_TX, h)).ToList());
    }
  }
}
