namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class PeerBToken : Peer
  {
    internal PeerBToken(
      NetworkBToken network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
      : base(network, socketCommunication, connection)
    {
      ProtocolStateMachine.Add(DBMessage.Command, new DBMessage(network.Blockchain));
      ProtocolStateMachine.Add(TXMessage.Command, new TXMessage(network.Blockchain, network));
    }
  }
}
