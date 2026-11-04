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

      TX tX = blockchainBToken.ParseTX(Payload, ref index, flagIsCoinbase: false);

      if (index != LengthDataPayload)
        throw new ProtocolException($"Length {index} of parsed TX not equal to payload length {LengthDataPayload}.");

      try
      {
        await blockchainBToken.RunLocked(() => blockchainBToken.AddTX(tX));

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

      Inventory.InventoryType typeInventory = Inventory.Parse(Payload, ref startIndex).Type;

      if (typeInventory == Inventory.InventoryType.MSG_DB)
      {
        if (peer.StateCurrent != Peer.StateProtocol.DBDownload)
          return;

        DOSMonitor.Decrement(1);

        peer.StateCurrent = Peer.StateProtocol.Idle;
        return;
      }

      if (typeInventory != Inventory.InventoryType.MSG_TX)
      {
        await base.Run(peer);
        return;
      }

      startIndex = startIndexInventories;
      bool isRequestAnswered = false;

      Dictionary<byte[], DateTime> hashesTXRequested = ((PeerBToken)peer).HashesTXRequested;

      lock (hashesTXRequested)
        for (int i = 0; i < inventoryCount; i++)
          if (hashesTXRequested.Remove(Inventory.Parse(Payload, ref startIndex).Hash))
            isRequestAnswered = true;

      if (isRequestAnswered)
        DOSMonitor.Decrement(1);
    }
  }

  class GetDataMessageBToken : GetDataMessage
  {
    protected override async Task ServeDB(Peer peer, Inventory inventory)
    {
      DOSMonitor.Increment(1);

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      byte[] dataDB = await blockchainBToken.RunLocked(() => blockchainBToken.GetAccountsSerializedAtBlock(inventory.Hash));

      if (dataDB != null)
        await peer.SocketCommunication.SendMessage(DBMessage.Command, dataDB.Length, dataDB);
      else
        await SendInventories(peer, NotFoundMessage.Command, [inventory]);
    }
  }

  class BlockMessageBToken : BlockMessage
  {
    internal BlockMessageBToken(Blockchain blockchain)
      : base(blockchain)
    { }

    internal override async Task RequestBlockNext(Peer peer)
    {
      await base.RequestBlockNext(peer);

      if (BlockDownload.Header != null)
        return;

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      byte[] hashDBMissing = await blockchainBToken.RunLocked(blockchainBToken.GetHashBlockDatabaseMissing);

      if (hashDBMissing == null)
        return;

      ((DBMessage)peer.MessagesByCommand[DBMessage.Command]).HashBlockDatabaseRequested = hashDBMissing;

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.DBDownload;

      await SendInventories(peer, GetDataMessage.Command, [new(Inventory.InventoryType.MSG_DB, hashDBMissing)]);
    }
  }

  class DBMessage : NetworkMessage
  {
    internal const string Command = "db";

    internal byte[] HashBlockDatabaseRequested;

    const int COUNT_MAX_ACCOUNTS = 1_000_000;
    const int SIZE_BUFFER_PAYLOAD = 9 + COUNT_MAX_ACCOUNTS * BlockchainBToken.Account.LENGTH_ACCOUNT;
    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal DBMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      if (peer.StateCurrent != Peer.StateProtocol.DBDownload)
        throw new ProtocolException($"Received unrequested db message.");

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      await blockchainBToken.RunLocked(() => blockchainBToken.InstallAccountsAtBlock(HashBlockDatabaseRequested, Payload, LengthDataPayload));

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
