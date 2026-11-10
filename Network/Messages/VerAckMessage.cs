namespace BTokenCore;

internal partial class Network
{
  protected class VerAckMessage : NetworkMessage
  {
    internal const string Command = "verack";

    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal VerAckMessage()
      : base(Array.Empty<byte>(), MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override async Task Run(Peer peer)
    {
      peer.StateCurrent = Peer.StateProtocol.Idle;
      peer.HandshakeCompleted.TrySetResult();
    }
  }
}