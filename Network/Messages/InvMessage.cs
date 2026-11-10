namespace BTokenCore;

internal partial class Network
{
  protected abstract class InvMessage : NetworkMessage
  {
    internal const string Command = "inv";

    internal const int COUNT_MAX_INVENTORIES = 100;

    const int SIZE_BUFFER_PAYLOAD = 3 + COUNT_MAX_INVENTORIES * 36;
    const int MAX_LEVEL_DOS = 240;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 150;


    internal InvMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }
  }
}