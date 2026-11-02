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
}
