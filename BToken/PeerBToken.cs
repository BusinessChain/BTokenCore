namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class PeerBToken : Peer
  {
    const int TIMESPAN_MEAN_INV_SECONDS = 5;

    BlockchainBToken BlockchainBToken;

    internal byte[] HashBlockAnnounced;

    internal Dictionary<byte[], DateTime> HashesTXRequested = new(new EqualityComparerByteArray());

    internal HashSet<byte[]> HashesTXQueued = new(new EqualityComparerByteArray());
    internal DateTime TimeInvNext;
    internal DOSMonitor ThrottleTXAnnouncedToPeer = new(
      TXMessage.MAX_LEVEL_ANNOUNCED,
      TXMessage.AMOUNT_DRAIN_DOS_PER_10_MINUTES);


    internal PeerBToken(
      NetworkBToken network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      BlockchainBToken = network.BlockchainBToken;

      ProtocolStateMachine.Add(DBMessage.Command, new DBMessage(BlockchainBToken));
      ProtocolStateMachine.Add(TXMessage.Command, new TXMessageBToken(network));
      ProtocolStateMachine.Add(HeadersMessage.Command, new HeadersMessageBToken(BlockchainBToken));
      ProtocolStateMachine.Add(InvMessage.Command, new InvMessageBToken(BlockchainBToken));
      ProtocolStateMachine.Add(VerAckMessage.Command, new VerAckMessage());
      ProtocolStateMachine.Add(NotFoundMessage.Command, new NotFoundMessageBToken());
    }

    internal override bool CanDeliverBlock(Header header)
    {
      bool isAnnouncedByPeer = HashBlockAnnounced != null && header.Hash.IsAllBytesEqual(HashBlockAnnounced);
      bool hasBlockHadTimeToSpread = !header.IsParentNewest();

      return base.CanDeliverBlock(header) && (hasBlockHadTimeToSpread || isAnnouncedByPeer);
    }

    internal override async Task RelayTXs()
    {
      lock (HashesTXRequested)
        if (HashesTXRequested.Values.Any(t => DateTime.UtcNow - t > TimeSpan.FromSeconds(TIMEOUT_REQUEST_SECONDS)))
          throw new ProtocolException($"Peer did not answer a TX request within {TIMEOUT_REQUEST_SECONDS} seconds.");

      if (DateTime.UtcNow < TimeInvNext)
        return;

      HashSet<byte[]> hashesQueued;

      lock (HashesTXQueued)
        hashesQueued = new(HashesTXQueued, new EqualityComparerByteArray());

      List<byte[]> hashesTX = new();

      if (hashesQueued.Count > 0)
      {
        List<TX> tXsRanked = await BlockchainBToken.GetTXsRanked(hashesQueued);

        foreach (TX tX in tXsRanked)
        {
          if (hashesTX.Count == InvMessage.MAX_COUNT_INVENTORIES || !ThrottleTXAnnouncedToPeer.TryIncrement(tX.CountBytes))
            break;

          hashesTX.Add(tX.Hash);
        }

        hashesQueued.ExceptWith(tXsRanked.Skip(hashesTX.Count).Select(t => t.Hash));

        lock (HashesTXQueued)
          HashesTXQueued.ExceptWith(hashesQueued);
      }

      if (hashesTX.Count > 0)
      {
        await NetworkMessage.SendInventories(
          this,
          InvMessage.Command,
          hashesTX.Select(h => new Inventory(Inventory.InventoryType.MSG_TX, h)).ToList());

        TimeInvNext = DateTime.UtcNow.AddSeconds(
          -TIMESPAN_MEAN_INV_SECONDS * Math.Log(1 - Random.Shared.NextDouble()));
      }
    }
  }
}
