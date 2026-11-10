namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class HeadersMessageBitcoin : HeadersMessage
  {
    internal override async Task Run(Peer peer)
    {
      int startIndex = 0;
      int countHeaders = VarInt.GetInt(Payload, ref startIndex);

      if (countHeaders > COUNT_MAX_HEADERS)
        throw new ProtocolException($"Too many headers {countHeaders} in headers message.");

      if (countHeaders == 0)
      {
        if (peer.StateCurrent == Peer.StateProtocol.HeaderDownload)
          peer.StateCurrent = Peer.StateProtocol.Idle;

        return;
      }

      List<Header> headers = new();

      for (int i = 0; i < countHeaders; i++)
      {
        headers.Add(peer.Network.Blockchain.ParseHeader(Payload, ref startIndex));
        VarInt.GetInt(Payload, ref startIndex);
      }

      Header headerTipReceivedLast = await peer.Network.Blockchain.RunLocked(() => peer.Network.Blockchain.InsertHeadersInTree(headers));

      if (headerTipReceivedLast != null)
        ((PeerBitcoin)peer).HeaderTipReceivedLast = headerTipReceivedLast;

      if (peer.StateCurrent == Peer.StateProtocol.HeaderDownload)
      {
        if (headerTipReceivedLast != null)
        {
          DOSMonitor.Decrement(1);
          await GetHeadersMessage.SendGetHeaders(peer, [headerTipReceivedLast.Hash]);
        }
        else
          peer.StateCurrent = Peer.StateProtocol.Idle;
      }
    }
  }
}
