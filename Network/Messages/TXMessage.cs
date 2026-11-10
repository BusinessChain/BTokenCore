namespace BTokenCore;

internal partial class Network
{
  protected abstract class TXMessage : NetworkMessage
  {
    internal const string Command = "tx";

    const int SIZE_BUFFER_PAYLOAD = 100_000;
    internal const int MAX_LEVEL_ANNOUNCED = 1_000_000;
    const int MARGIN_LEVEL_DOS = 1_000_000;
    internal const int MAX_LEVEL_DOS = MAX_LEVEL_ANNOUNCED + MARGIN_LEVEL_DOS;
    internal const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1_000_000;

    internal DateTime TimeDeliveredLast;


    internal TXMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }
  }
}