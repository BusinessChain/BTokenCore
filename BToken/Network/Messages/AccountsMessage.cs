namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class AccountsMessage : NetworkMessage
  {
    internal const string CommandSegment = "segment";

    const int COUNT_MAX_ACCOUNTS = 1_000_000;
    const int SIZE_BUFFER_SEGMENT = 9 + COUNT_MAX_ACCOUNTS * BlockchainBToken.Account.LENGTH_ACCOUNT;
    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal AccountsMessage()
      : base(
          [],
          MAX_LEVEL_DOS,
          AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override byte[] GetPayloadBuffer()
    {
      Payload = new byte[SIZE_BUFFER_SEGMENT];

      return Payload;
    }

    internal override async Task Run(Peer peer)
    {
      Inventory inventoryRequested = ((PeerBToken)peer).InventoryAccountsRequested;

      if (peer.StateCurrent != Peer.StateProtocol.AccountsDownload || inventoryRequested.Type != Inventory.InventoryType.MSG_SEGMENT_SWEEP)
        throw new ProtocolException($"Received unrequested {CommandSegment} message.");

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      await blockchainBToken.RunLocked(() => blockchainBToken.AddSegmentDownload(inventoryRequested.Hash, Payload, LengthDataPayload));

      Payload = null;

      BlockMessageBToken blockMessage = (BlockMessageBToken)peer.MessagesByCommand[BlockMessage.Command];

      blockMessage.RefundAccountsReceived(inventoryRequested);

      DOSMonitor.Decrement(1);

      await blockMessage.RequestBlockNext(peer);
    }
  }
}
