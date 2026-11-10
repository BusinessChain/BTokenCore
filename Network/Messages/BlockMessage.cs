namespace BTokenCore;

internal partial class Network
{
  protected class BlockMessage : NetworkMessage
  {
    internal const string Command = "block";

    internal Block BlockDownload;
    internal DateTime TimeRequest;
    internal DateTime TimeDeliveredLast;

    const int MAX_LEVEL_DOS = 5;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 5;

    protected DOSMonitor DOSMonitorRequests = new(
      GetDataMessage.MAX_LEVEL_DOS,
      GetDataMessage.AMOUNT_DRAIN_DOS_PER_10_MINUTES);

    Header HeaderBlockReceivedLast;


    internal BlockMessage(Blockchain blockchain)
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      BlockDownload = new(blockchain.SizeBlockMax);
    }

    internal override byte[] GetPayloadBuffer()
    {
      return BlockDownload.Buffer;
    }

    internal override async Task Run(Peer peer)
    {
      if (BlockDownload?.Header == null)
        throw new ProtocolException($"Received unrequested block message.");

      BlockDownload.LengthDataPayload = LengthDataPayload;

      await peer.Network.Blockchain.RunLocked(() =>
      {
        if (GetDataMessage.RefundBlockServed(ref HeaderBlockReceivedLast, BlockDownload.Header))
          DOSMonitorRequests.Decrement(1);

        peer.Network.Blockchain.ReceiveBlock(BlockDownload);
      });

      TimeDeliveredLast = DateTime.UtcNow;

      DOSMonitor.Decrement(1);

      await RequestBlockNext(peer);
    }

    internal async Task RequestBlockNext(Peer peer)
    {
      if (DOSMonitorRequests.TryIncrement(1))
      {
        if (await TryRequestItemNext(peer))
          return;

        DOSMonitorRequests.Decrement(1);
      }

      BlockDownload.Header = null;
      peer.StateCurrent = Peer.StateProtocol.Idle;
    }

    protected virtual async Task<bool> TryRequestItemNext(Peer peer)
    {
      BlockDownload.Header = await peer.Network.Blockchain.RunLocked(() =>
        peer.Network.Blockchain.FetchHeaderBlockMissingNext(peer.CanDeliverBlock));

      if (BlockDownload.Header == null)
        return false;

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.BlockDownload;

      await SendInventories(peer, GetDataMessage.Command, [new(Inventory.InventoryType.MSG_BLOCK, BlockDownload.Header.Hash)]);

      return true;
    }
  }
}