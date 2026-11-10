namespace BTokenCore;

internal partial class Network
{
  protected abstract class HeadersMessage : NetworkMessage
  {
    internal const int COUNT_MAX_HEADERS = 2000;
    internal const string Command = "headers";

    const int SIZE_BUFFER_PAYLOAD = 3 + COUNT_MAX_HEADERS * 101;
    const int MAX_LEVEL_DOS = 20;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 2;


    internal HeadersMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal static async Task SendHeaders(Peer peer, List<byte[]> headersSerialized)
    {
      await SendItems(peer, Command, headersSerialized.Select(byte[] (h) => [.. h, 0x00]).ToList());
    }
  }
}