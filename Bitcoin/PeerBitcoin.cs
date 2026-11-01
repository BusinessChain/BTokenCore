namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class PeerBitcoin : Peer
  {
    internal PeerBitcoin(
      NetworkBitcoin network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      ProtocolStateMachine.Add(GetHeadersMessage.Command, new GetHeadersMessage(network.Blockchain));
    }
  }
}
