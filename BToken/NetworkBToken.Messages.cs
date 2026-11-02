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
    BlockchainBToken BlockchainBToken;


    internal InvMessageBToken(BlockchainBToken blockchain)
    {
      BlockchainBToken = blockchain;
    }

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

      List<byte[]> hashesTXNotInPool = await BlockchainBToken.RunLocked(() => BlockchainBToken.GetHashesTXNotInPool(hashesTX));

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
    NetworkBToken Network;


    internal TXMessageBToken(NetworkBToken network)
    {
      Network = network;
    }

    internal override void IncrementDOSMonitor()
    {
      DOSMonitor.Increment(LengthDataPayload);
    }

    internal override async Task Run(Peer peer)
    {
      int index = 0;

      TX tX = Network.BlockchainBToken.ParseTX(Payload, ref index, flagIsCoinbase: false);

      if (index != LengthDataPayload)
        throw new ProtocolException($"Length {index} of parsed TX not equal to payload length {LengthDataPayload}.");

      try
      {
        await Network.AddTX(tX);
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
    BlockchainBToken BlockchainBToken;


    internal GetDataMessageBToken(BlockchainBToken blockchain)
      : base(blockchain)
    {
      BlockchainBToken = blockchain;
    }

    protected override async Task ServeDB(Peer peer, Inventory inventory)
    {
      DOSMonitor.Increment(1);

      byte[] dataDB = await BlockchainBToken.RunLocked(() => BlockchainBToken.GetDB(inventory.Hash));

      if (dataDB != null)
        await peer.SocketCommunication.SendMessage(DBMessage.Command, dataDB.Length, dataDB);
      else
        await SendInventories(peer, NotFoundMessage.Command, [inventory]);
    }
  }

  class BlockMessageBToken : BlockMessage
  {
    BlockchainBToken BlockchainBToken;


    internal BlockMessageBToken(BlockchainBToken blockchain)
      : base(blockchain)
    {
      BlockchainBToken = blockchain;
    }

    internal override async Task RequestBlockNext(Peer peer)
    {
      await base.RequestBlockNext(peer);

      if (BlockDownload.Header != null)
        return;

      byte[] hashDBMissing = await BlockchainBToken.RunLocked(BlockchainBToken.GetHashDBMissing);

      if (hashDBMissing == null)
        return;

      ((DBMessage)peer.ProtocolStateMachine[DBMessage.Command]).HashDBRequested = hashDBMissing;

      TimeRequest = DateTime.UtcNow;
      peer.StateCurrent = Peer.StateProtocol.DBDownload;

      await SendInventories(peer, GetDataMessage.Command, [new(Inventory.InventoryType.MSG_DB, hashDBMissing)]);
    }
  }

  class DBMessage : NetworkMessage
  {
    internal const string Command = "db";

    internal byte[] HashDBRequested;

    BlockchainBToken BlockchainBToken;

    const int MAX_COUNT_ACCOUNTS = 1_000_000;
    const int SIZE_BUFFER_PAYLOAD = 9 + MAX_COUNT_ACCOUNTS * BlockchainBToken.Account.LENGTH_ACCOUNT;
    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal DBMessage(BlockchainBToken blockchain)
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    {
      BlockchainBToken = blockchain;
    }

    internal override async Task Run(Peer peer)
    {
      if (peer.StateCurrent != Peer.StateProtocol.DBDownload)
        throw new ProtocolException($"Received unrequested db message.");

      await BlockchainBToken.RunLocked(() => BlockchainBToken.InstallDB(HashDBRequested, Payload, LengthDataPayload));

      DOSMonitor.Decrement(1);

      peer.StateCurrent = Peer.StateProtocol.Idle;
    }
  }
}
