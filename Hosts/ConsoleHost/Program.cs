using BTokenCore;

namespace ConsoleHost;

class Program
{
  const int PORT_RPC_DEFAULT = 8778;


  static async Task Main(string[] args)
  {
    int portRPC = args.Length > 0 ? int.Parse(args[0]) : PORT_RPC_DEFAULT;

    Node node = new(new CommunicationTCP(), portRPC);

    await node.Start();

    await Task.Delay(Timeout.Infinite);
  }
}
