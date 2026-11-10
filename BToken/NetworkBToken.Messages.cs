using System.Security.Cryptography;

namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class HeadersMessageBToken : HeadersMessage
  {
    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;
      int countHeaders = VarInt.GetInt(Payload, ref startIndex);

      if (countHeaders == 1)
        ((PeerBToken)peer).HashBlockAnnounced = peer.Network.Blockchain.ParseHeader(Payload, ref startIndex).Hash;
    }
  }

  class InvMessageBToken : InvMessage
  {
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

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      List<byte[]> hashesTXNotInPool = await blockchainBToken.RunLocked(() => blockchainBToken.GetHashesTXNotInPool(hashesTX));

      Dictionary<byte[], DateTime> hashesTXRequested = ((PeerBToken)peer).HashesTXRequested;

      lock (hashesTXRequested)
        foreach (byte[] hashTX in hashesTXNotInPool)
          hashesTXRequested.TryAdd(hashTX, DateTime.UtcNow);

      if (hashesTXNotInPool.Count > 0)
        await SendInventories(
          peer,
          GetDataMessage.Command,
          hashesTXNotInPool.Select(h => new Inventory(Inventory.InventoryType.MSG_TX, h)).ToList());
    }
  }

  class TXMessageBToken : TXMessage
  {
    internal override void IncrementDOSMonitor()
    {
      DOSMonitor.Increment(LengthDataPayload);
    }

    internal override async Task Run(Peer peer)
    {
      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      int index = 0;

      TX tX = blockchainBToken.ParseTX(Payload, ref index, isCoinbase: false, verifySignatures: true);

      if (index != LengthDataPayload)
        throw new ProtocolException($"Length {index} of parsed TX not equal to payload length {LengthDataPayload}.");

      try
      {
        await blockchainBToken.RunLocked(() => blockchainBToken.AddTX(tX));

        TimeDeliveredLast = DateTime.UtcNow;

        ((NetworkBToken)peer.Network).QueueTXForRelay(tX);
      }
      catch (ProtocolException)
      { }

      Dictionary<byte[], DateTime> hashesTXRequested = ((PeerBToken)peer).HashesTXRequested;

      lock (hashesTXRequested)
        hashesTXRequested.Remove(tX.Hash);
    }
  }

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

  class GetDataMessageBToken : GetDataMessage
  {
    HashSet<byte[]> HashesAccountsServed = new(new EqualityComparerByteArray());


    protected override async Task ServeAccounts(Peer peer, Inventory inventory)
    {
      DOSMonitor.Increment(1);

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;
      bool isHashesSegment = inventory.Type == Inventory.InventoryType.MSG_SEGMENT_HASHES;

      byte[] payload = await blockchainBToken.RunLocked(() => isHashesSegment
        ? blockchainBToken.GetHashesSegmentAtBlock(inventory.Hash)
        : blockchainBToken.GetSegmentServed(inventory.Hash));

      if (payload == null)
      {
        await SendInventories(peer, NotFoundMessage.Command, [inventory]);
        return;
      }

      if (RefundAccountsServed(HashesAccountsServed, inventory))
        DOSMonitor.Decrement(1);

      await peer.SocketCommunication.SendMessage(
        isHashesSegment ? AccountsMessage.CommandHashesSegment : AccountsMessage.CommandSegment,
        payload.Length,
        payload);
    }

    internal static bool RefundAccountsServed(HashSet<byte[]> hashesAccountsServed, Inventory inventory)
    {
      if (inventory.Type == Inventory.InventoryType.MSG_SEGMENT_HASHES && !hashesAccountsServed.Contains(inventory.Hash))
        hashesAccountsServed.Clear();

      return hashesAccountsServed.Add(inventory.Hash);
    }
  }

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

  class FeeFilterMessage : NetworkMessage
  {
    internal const string Command = "feefilter";

    const int SIZE_BUFFER_PAYLOAD = 8;
    const int MAX_LEVEL_DOS = 30;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 30;


    internal FeeFilterMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal static async Task SendFeeFilter(Peer peer, double feeFilter)
    {
      byte[] payload = BitConverter.GetBytes(feeFilter);

      await peer.SocketCommunication.SendMessage(Command, payload.Length, payload);
    }

    internal override async Task Run(Peer peer)
    {
      if (LengthDataPayload != SIZE_BUFFER_PAYLOAD)
        throw new ProtocolException($"Length {LengthDataPayload} of feefilter payload not equal to {SIZE_BUFFER_PAYLOAD}.");

      ((PeerBToken)peer).FeeFilterReceived = BitConverter.ToDouble(Payload);
    }
  }
}
