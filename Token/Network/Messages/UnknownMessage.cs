namespace BTokenCore;

internal partial class Network
{
  class UnknownMessage : NetworkMessage
  {
    internal const string Command = "commandUnknown";

    const int SIZE_BUFFER_PAYLOAD = 10_000;
    const int MAX_LEVEL_DOS = 100;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 100;


    internal UnknownMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {

    }
  }
}