namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class VerAckMessageBitcoin : VerAckMessage
  {
    internal override async Task Run(Peer peer)
    {
      await base.Run(peer);

      if (peer.Connection == Peer.ConnectionType.OUTBOUND)
        await GetHeadersMessage.SendGetHeaders(peer, await peer.Network.Blockchain.RunLocked(peer.Network.Blockchain.GetLocator));
    }
  }
}
