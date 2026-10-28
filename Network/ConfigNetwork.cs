namespace BTokenCore;

internal class ConfigNetwork
{
  internal const ulong NODE_NETWORK = 1;

  internal int Port;
  internal UInt32 ProtocolVersion = 70015;
  internal ulong NetworkServicesLocal = 0;
  internal ulong NetworkServicesRemote = 0;
  internal string UserAgent = "/BTokenCore:0.0.0/";
  internal string[] SeedAddresses = [];
  internal bool EnableInboundConnections;
  internal bool EnableRelay;
  internal bool IsProtocolBitcoin;
}
