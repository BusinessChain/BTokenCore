namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  internal NetworkBToken(
    BlockchainBToken blockchain,
    ICommunication communication)
    : base(
      blockchain,
      communication,
      new Configuration()
      {
        Port = 8777,
        EnableInboundConnections = true,
        EnableRelay = true
      })
  { }
}
