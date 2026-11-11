namespace BTokenCore;

internal partial class Network
{
  protected class GetDataMessage : NetworkMessage
  {
    internal const string Command = "getdata";

    Header HeaderBlockDownloadedTip;

    const int SIZE_BUFFER_PAYLOAD = 36_003;
    internal const int MAX_LEVEL_DOS = 5;
    internal const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;

    DOSMonitor DOSMonitorTX = new(
      TXMessage.MAX_LEVEL_DOS,
      TXMessage.AMOUNT_DRAIN_DOS_PER_10_MINUTES);


    internal GetDataMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override void IncrementDOSMonitor()
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      int inventoryCount = VarInt.GetInt(Payload, ref startIndex);

      List<Inventory> inventoriesTXNotFound = new();

      for (int i = 0; i < inventoryCount; i++)
      {
        Inventory inventory = Inventory.Parse(Payload, ref startIndex);

        if (inventory.Type == Inventory.InventoryType.MSG_TX)
        {
          TX tX = await peer.Network.Blockchain.RunLocked(() => peer.Network.Blockchain.GetTX(inventory.Hash));

          if (tX != null)
          {
            DOSMonitorTX.Increment(tX.TXRaw.Length);
            await peer.SocketCommunication.SendMessage(TXMessage.Command, tX.TXRaw.Length, tX.TXRaw);
          }
          else
          {
            DOSMonitorTX.Increment(Inventory.LENGTH);
            inventoriesTXNotFound.Add(inventory);
          }
        }
        else if (inventory.Type == Inventory.InventoryType.MSG_BLOCK)
        {
          DOSMonitor.Increment(1);

          Block blockUpload = await peer.Network.Blockchain.RunLocked(() =>
          {
            Block block = peer.Network.Blockchain.GetBlock(inventory.Hash);

            if (block != null && RefundBlockServed(ref HeaderBlockDownloadedTip, block.Header))
              DOSMonitor.Decrement(1);

            return block;
          });

          if (blockUpload != null)
            await peer.SocketCommunication.SendMessage(BlockMessage.Command, blockUpload.LengthDataPayload, blockUpload.Buffer);
          else
            await SendInventories(peer, NotFoundMessage.Command, [inventory]);
        }
        else if (inventory.Type == Inventory.InventoryType.MSG_SEGMENT_SWEEP)
          await ServeAccounts(peer, inventory);
      }

      if (inventoriesTXNotFound.Count > 0)
        await SendInventories(peer, NotFoundMessage.Command, inventoriesTXNotFound);
    }

    internal static bool RefundBlockServed(ref Header headerBlockServedLast, Header header)
    {
      if (headerBlockServedLast?.GetHeaderAncestor(header.Height) == header)
        return false;

      headerBlockServedLast = header;
      return true;
    }

    protected virtual Task ServeAccounts(Peer peer, Inventory inventory)
    {
      return Task.CompletedTask;
    }
  }
}