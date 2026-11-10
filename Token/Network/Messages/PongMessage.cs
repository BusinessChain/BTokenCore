namespace BTokenCore;

internal partial class Network
{
  class PongMessage : NetworkMessage
  {
    internal const string Command = "pong";

    const int SIZE_BUFFER_PAYLOAD = 8;
    const int MAX_LEVEL_DOS = 10;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 10;


    internal PongMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      PingMessage messagePing = (PingMessage)peer.MessagesByCommand[PingMessage.Command];

      if (messagePing.IsPongPending
        && LengthDataPayload == SIZE_BUFFER_PAYLOAD
        && BitConverter.ToUInt64(Payload) == messagePing.Nonce)
        messagePing.IsPongPending = false;
    }
  }
}