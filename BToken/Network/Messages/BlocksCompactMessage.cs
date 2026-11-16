namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class BlocksCompactMessage : NetworkMessage
  {
    internal const string Command = "compacts";

    internal const int COUNT_MAX_BLOCKS_COMPACT = 2000;
    internal const int SIZE_BUFFER_PAYLOAD = 4_000_000;

    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal BlocksCompactMessage()
      : base(
          [],
          MAX_LEVEL_DOS,
          AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override byte[] GetPayloadBuffer()
    {
      Payload = new byte[SIZE_BUFFER_PAYLOAD];

      return Payload;
    }

    internal override Task Run(Peer peer)
    {
      throw new ProtocolException($"Received unrequested {Command} message.");
    }
  }
}
