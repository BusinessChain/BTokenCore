using BTokenCore;

namespace ConsoleHost;

class Program
{
  const int PORT_RPC_DEFAULT = 8778;
  const string ARGUMENT_OFFLINE = "offline";


  static async Task Main(string[] args)
  {
    string[] argsPort = args.Where(a => a != ARGUMENT_OFFLINE).ToArray();
    int portRPC = argsPort.Length > 0 ? int.Parse(argsPort[0]) : PORT_RPC_DEFAULT;

    Node node = new(new CommunicationTCP());

    if (!args.Contains(ARGUMENT_OFFLINE))
      await node.Start();

    await new RPCServer(node, portRPC).Start();
  }
}
