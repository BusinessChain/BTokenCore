namespace BTokenCore;

internal partial class Network
{
  class PingMessage : NetworkMessage
  {
    internal const string Command = "ping";

    internal UInt64 Nonce;
    internal bool IsPongPending;
    DateTime TimePingSent;


    const int SIZE_BUFFER_PAYLOAD = 8;
    const int MAX_LEVEL_DOS = 10;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 10;

    const int INTERVAL_PING_SECONDS = 120;
    const int TIMEOUT_PONG_SECONDS = 1200;


    internal PingMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal async Task Ping(Peer peer)
    {
      if (IsPongPending)
      {
        if (DateTime.UtcNow - TimePingSent > TimeSpan.FromSeconds(TIMEOUT_PONG_SECONDS))
          peer.SocketCommunication.Dispose();
      }
      else if (DateTime.UtcNow - TimePingSent > TimeSpan.FromSeconds(INTERVAL_PING_SECONDS))
      {
        Nonce = (UInt64)Random.Shared.NextInt64();
        TimePingSent = DateTime.UtcNow;
        IsPongPending = true;

        byte[] payload = BitConverter.GetBytes(Nonce);

        await peer.SocketCommunication.SendMessage(Command, payload.Length, payload);
      }
    }

    internal override async Task Run(Peer peer)
    {
      await peer.SocketCommunication.SendMessage(PongMessage.Command, LengthDataPayload, Payload);
    }
  }
}