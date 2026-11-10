using System.Security.Cryptography;

namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class AccountsMessage : NetworkMessage
  {
    internal const string CommandHashesSegment = "seghashes";
    internal const string CommandSegment = "segment";

    Inventory.InventoryType TypeInventory;

    const int COUNT_MAX_ACCOUNTS = 1_000_000;
    const int SIZE_BUFFER_SEGMENT = 9 + COUNT_MAX_ACCOUNTS * BlockchainBToken.Account.LENGTH_ACCOUNT;
    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal AccountsMessage(Inventory.InventoryType typeInventory)
      : base(
          new byte[typeInventory == Inventory.InventoryType.MSG_SEGMENT_HASHES ? BlockchainBToken.LENGTH_HASHES_SEGMENT : 0],
          MAX_LEVEL_DOS,
          AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      TypeInventory = typeInventory;
    }

    internal override byte[] GetPayloadBuffer()
    {
      if (TypeInventory == Inventory.InventoryType.MSG_SEGMENT)
        Payload = new byte[SIZE_BUFFER_SEGMENT];

      return Payload;
    }

    internal override async Task Run(Peer peer)
    {
      Inventory inventoryRequested = ((PeerBToken)peer).InventoryAccountsRequested;

      if (peer.StateCurrent != Peer.StateProtocol.AccountsDownload || inventoryRequested.Type != TypeInventory)
        throw new ProtocolException($"Received unrequested {TypeInventory} message.");

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      if (TypeInventory == Inventory.InventoryType.MSG_SEGMENT_HASHES)
      {
        if (LengthDataPayload != Payload.Length)
          throw new ProtocolException($"Length {LengthDataPayload} of segment hashes not equal to {Payload.Length}.");

        await blockchainBToken.RunLocked(() => blockchainBToken.SetHashesSegmentDownload(inventoryRequested.Hash, Payload));
      }
      else
      {
        if (!SHA256.HashData(Payload.AsSpan(0, LengthDataPayload)).IsAllBytesEqual(inventoryRequested.Hash))
          peer.HashesSegmentRefused.Add(inventoryRequested.Hash);
        else
          await blockchainBToken.RunLocked(() => blockchainBToken.AddSegmentDownload(inventoryRequested.Hash, Payload, LengthDataPayload));

        Payload = null;
      }

      ((BlockMessageBToken)peer.MessagesByCommand[BlockMessage.Command]).RefundAccountsReceived(inventoryRequested);

      DOSMonitor.Decrement(1);

      peer.StateCurrent = Peer.StateProtocol.Idle;
    }
  }
}
