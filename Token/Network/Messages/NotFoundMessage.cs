namespace BTokenCore;

internal partial class Network
{
  protected class NotFoundMessage : NetworkMessage
  {
    internal const string Command = "notfound";

    const int SIZE_BUFFER_PAYLOAD = 36_003;
    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;


    internal NotFoundMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;

      VarInt.GetInt(Payload, ref startIndex);
      Inventory inventory = Inventory.Parse(Payload, ref startIndex);

      BlockMessage blockMessage = (BlockMessage)peer.MessagesByCommand[BlockMessage.Command];

      if (blockMessage.BlockDownload.Header?.Hash.IsAllBytesEqual(inventory.Hash) != true)
        return;

      DOSMonitor.Decrement(1);

      peer.HashesBlockRefused.TryGetValue(inventory.Hash, out (int countRefusals, DateTime timeRefusedLast) refusal);
      peer.HashesBlockRefused[inventory.Hash] = (refusal.countRefusals + 1, DateTime.UtcNow);

      blockMessage.BlockDownload.Header = null;
      peer.StateCurrent = Peer.StateProtocol.Idle;
    }
  }
}