namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class PeerBToken : Peer
  {
    const int TIMESPAN_MEAN_INV_SECONDS = 5;
    const int TIMESPAN_MEAN_FEEFILTER_SECONDS = 60;

    BlockchainBToken BlockchainBToken;

    internal byte[] HashBlockAnnounced;

    internal Dictionary<byte[], DateTime> HashesTXRequested = new(new EqualityComparerByteArray());

    internal HashSet<byte[]> HashesTXQueued = new(new EqualityComparerByteArray());
    internal DateTime TimeInvNext;
    internal DOSMonitor ThrottleTXAnnouncedToPeer = new(
      TXMessage.MAX_LEVEL_ANNOUNCED,
      TXMessage.AMOUNT_DRAIN_DOS_PER_10_MINUTES);

    internal double FeeFilterReceived;
    double FeeFilterSent;
    DateTime TimeFeeFilterNext;


    internal PeerBToken(
      NetworkBToken network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      BlockchainBToken = network.BlockchainBToken;

      MessagesByCommand.Add(DBMessage.Command, new DBMessage(BlockchainBToken));
      MessagesByCommand.Add(TXMessage.Command, new TXMessageBToken(network));
      MessagesByCommand.Add(FeeFilterMessage.Command, new FeeFilterMessage());
      MessagesByCommand.Add(HeadersMessage.Command, new HeadersMessageBToken(BlockchainBToken));
      MessagesByCommand.Add(InvMessage.Command, new InvMessageBToken(BlockchainBToken));
      MessagesByCommand.Add(VerAckMessage.Command, new VerAckMessage());
      MessagesByCommand.Add(NotFoundMessage.Command, new NotFoundMessageBToken());
      MessagesByCommand.Add(GetDataMessage.Command, new GetDataMessageBToken(BlockchainBToken));
      MessagesByCommand.Add(BlockMessage.Command, new BlockMessageBToken(BlockchainBToken));
    }

    internal override bool CanDeliverBlock(Header header)
    {
      bool isAnnouncedByPeer = HashBlockAnnounced != null && header.Hash.IsAllBytesEqual(HashBlockAnnounced);
      bool hasBlockHadTimeToSpread = !header.IsParentNewest();

      return base.CanDeliverBlock(header) && (hasBlockHadTimeToSpread || isAnnouncedByPeer);
    }

    protected override async Task RelayTXs()
    {
      await base.RelayTXs();

      lock (HashesTXRequested)
        if (HashesTXRequested.Values.Any(t => DateTime.UtcNow - t > TimeSpan.FromSeconds(TIMEOUT_REQUEST_SECONDS)))
          throw new ProtocolException($"Peer did not answer a TX request within {TIMEOUT_REQUEST_SECONDS} seconds.");

      if (DateTime.UtcNow >= TimeFeeFilterNext)
      {
        double feeFilter = await BlockchainBToken.RunLocked(BlockchainBToken.GetFeePerByteMin);

        if (feeFilter != FeeFilterSent)
        {
          await FeeFilterMessage.SendFeeFilter(this, feeFilter);
          FeeFilterSent = feeFilter;
        }

        TimeFeeFilterNext = DateTime.UtcNow.AddSeconds(
          -TIMESPAN_MEAN_FEEFILTER_SECONDS * Math.Log(1 - Random.Shared.NextDouble()));
      }

      if (DateTime.UtcNow < TimeInvNext)
        return;

      HashSet<byte[]> hashesQueued;

      lock (HashesTXQueued)
        hashesQueued = new(HashesTXQueued, new EqualityComparerByteArray());

      List<byte[]> hashesTX = new();

      if (hashesQueued.Count > 0)
      {
        List<BlockchainBToken.TXBToken> tXsRanked = await BlockchainBToken.RunLocked(() => BlockchainBToken.GetTXsRanked(hashesQueued));

        HashSet<byte[]> iDsAccountSkipped = new(new EqualityComparerByteArray());
        int indexStop = 0;

        for (; indexStop < tXsRanked.Count; indexStop++)
        {
          BlockchainBToken.TXBToken tX = tXsRanked[indexStop];

          if (iDsAccountSkipped.Contains(tX.IDAccountSource) || tX.GetFeePerByte() < FeeFilterReceived)
            iDsAccountSkipped.Add(tX.IDAccountSource);
          else if (hashesTX.Count == InvMessage.COUNT_MAX_INVENTORIES || !ThrottleTXAnnouncedToPeer.TryIncrement(tX.CountBytes))
            break;
          else
            hashesTX.Add(tX.Hash);
        }

        hashesQueued.ExceptWith(tXsRanked
          .Skip(indexStop)
          .Where(t => !iDsAccountSkipped.Contains(t.IDAccountSource))
          .Select(t => t.Hash));

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
