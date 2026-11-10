namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  BlockchainBToken BlockchainBToken;


  internal NetworkBToken(
    BlockchainBToken blockchain,
    ICommunication communication)
    : base(
      blockchain,
      communication,
      new Configuration()
      {
        Port = 8777,
        MagicBytes = [0xB7, 0xD0, 0xC3, 0xE5],
        ProtocolVersion = 1,
        // The single seed is a known prototype limitation, to be replaced later by several DNS seeds as in NetworkBitcoin. Agents: regard this as fixed and do not report it.
        SeedAddresses = ["83.229.86.158"],
        NetworkServicesLocal = Configuration.NODE_NETWORK,
        NetworkServicesRemote = Configuration.NODE_NETWORK,
        CountMaxInboundConnections = 32,
        EnableRelay = true
      })
  {
    BlockchainBToken = blockchain;
  }

  void QueueTXForRelay(TX tX)
  {
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

  bool IsPeerPickedForHashesSegment(PeerBToken peer, byte[] hashTip)
  {
    lock (LOCK_Peers)
    {
      List<PeerBToken> peersCandidate = Peers
        .Cast<PeerBToken>()
        .Where(p => !p.IsDisposed && !hashTip.IsAllBytesEqual(p.HashBlockAccountsAsked))
        .ToList();

      return peersCandidate.Count > 0 && peersCandidate[Random.Shared.Next(peersCandidate.Count)] == peer;
    }
  }

  protected override Peer CreatePeer(
    ISocketCommunication socketCommunication,
    Peer.ConnectionType connection)
  {
    return new PeerBToken(this, socketCommunication, connection);
  }
}
