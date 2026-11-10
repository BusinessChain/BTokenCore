namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class TXMessageBToken : TXMessage
  {
    internal override void IncrementDOSMonitor()
    {
      DOSMonitor.Increment(LengthDataPayload);
    }

    internal override async Task Run(Peer peer)
    {
      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      int index = 0;

      TX tX = blockchainBToken.ParseTX(Payload, ref index, isCoinbase: false, verifySignatures: true);

      if (index != LengthDataPayload)
        throw new ProtocolException($"Length {index} of parsed TX not equal to payload length {LengthDataPayload}.");

      try
      {
        await blockchainBToken.RunLocked(() => blockchainBToken.AddTX(tX));

        TimeDeliveredLast = DateTime.UtcNow;

        ((NetworkBToken)peer.Network).QueueTXForRelay(tX);
      }
      catch (ProtocolException)
      { }

      Dictionary<byte[], DateTime> hashesTXRequested = ((PeerBToken)peer).HashesTXRequested;

      lock (hashesTXRequested)
        hashesTXRequested.Remove(tX.Hash);
    }
  }
}
