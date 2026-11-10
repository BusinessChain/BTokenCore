namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class FeeFilterMessage : NetworkMessage
  {
    internal const string Command = "feefilter";

    const int SIZE_BUFFER_PAYLOAD = 8;
    const int MAX_LEVEL_DOS = 30;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 30;


    internal FeeFilterMessage()
      : base(new byte[SIZE_BUFFER_PAYLOAD], MAX_LEVEL_DOS, AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal static async Task SendFeeFilter(Peer peer, double feeFilter)
    {
      byte[] payload = BitConverter.GetBytes(feeFilter);

      await peer.SocketCommunication.SendMessage(Command, payload.Length, payload);
    }

    internal override async Task Run(Peer peer)
    {
      if (LengthDataPayload != SIZE_BUFFER_PAYLOAD)
        throw new ProtocolException($"Length {LengthDataPayload} of feefilter payload not equal to {SIZE_BUFFER_PAYLOAD}.");

      ((PeerBToken)peer).FeeFilterReceived = BitConverter.ToDouble(Payload);
    }
  }
}
