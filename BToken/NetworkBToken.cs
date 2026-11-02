namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  const int TIMESPAN_MEAN_INV_SECONDS = 5;


  internal NetworkBToken(
    BlockchainBToken blockchain,
    ICommunication communication)
    : base(
      blockchain,
      communication,
      new Configuration()
      {
        Port = 8777,
        EnableInboundConnections = true,
        EnableRelay = true
      })
  { }

  internal async Task SubmitTX(byte[] tXRaw)
  {
    int index = 0;

    TX tX = Blockchain.ParseTX(tXRaw, ref index, flagIsCoinbase: false);

    await AddTX(tX);
  }

  async Task AddTX(TX tX)
  {
    await Blockchain.AddTX(tX);

    lock (LOCK_Peers)
      foreach (PeerBToken peer in Peers)
      {
        lock (peer.HashesTXRequested)
          if (peer.HashesTXRequested.ContainsKey(tX.Hash))
            continue;

        lock (peer.HashesTXQueued)
          peer.HashesTXQueued.Add(tX.Hash);
      }
  }

  protected override Peer CreatePeer(
    ISocketCommunication socketCommunication,
    Peer.ConnectionType connection)
  {
    return new PeerBToken(this, socketCommunication, connection);
  }

  protected override async Task RelayTXs(Peer peer)
  {
    PeerBToken peerBToken = (PeerBToken)peer;

    lock (peerBToken.HashesTXRequested)
      if (peerBToken.HashesTXRequested.Values.Any(t => DateTime.UtcNow - t > TimeSpan.FromSeconds(TIMEOUT_REQUEST_SECONDS)))
        throw new ProtocolException($"Peer did not answer a TX request within {TIMEOUT_REQUEST_SECONDS} seconds.");

    if (DateTime.UtcNow < peerBToken.TimeInvNext)
      return;

    HashSet<byte[]> hashesQueued;

    lock (peerBToken.HashesTXQueued)
      hashesQueued = new(peerBToken.HashesTXQueued, new EqualityComparerByteArray());

    List<byte[]> hashesTX = new();

    if (hashesQueued.Count > 0)
    {
      List<TX> tXsRanked = await Blockchain.GetTXsRanked(hashesQueued);

      foreach (TX tX in tXsRanked)
      {
        if (hashesTX.Count == InvMessage.MAX_COUNT_INVENTORIES || !peerBToken.ThrottleTXAnnouncedToPeer.TryIncrement(tX.CountBytes))
          break;

        hashesTX.Add(tX.Hash);
      }

      hashesQueued.ExceptWith(tXsRanked.Skip(hashesTX.Count).Select(t => t.Hash));

      lock (peerBToken.HashesTXQueued)
        peerBToken.HashesTXQueued.ExceptWith(hashesQueued);
    }

    if (hashesTX.Count > 0)
    {
      await NetworkMessage.SendInventories(
        peer,
        InvMessage.Command,
        hashesTX.Select(h => new Inventory(Inventory.InventoryType.MSG_TX, h)).ToList());

      peerBToken.TimeInvNext = DateTime.UtcNow.AddSeconds(
        -TIMESPAN_MEAN_INV_SECONDS * Math.Log(1 - Random.Shared.NextDouble()));
    }
  }
}
