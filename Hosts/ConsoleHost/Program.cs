using System.Text;
using BTokenCore;

namespace ConsoleHost;

class Program
{
  const int PORT_RPC_DEFAULT = 8778;
  const string ARGUMENT_OFFLINE = "offline";
  const string NAME_FILE_LOG = "node.log";


  static async Task Main(string[] args)
  {
    Console.SetOut(new WriterLog(Console.Out, new StreamWriter(NAME_FILE_LOG, append: true) { AutoFlush = true }));

    string[] argsPort = args.Where(a => a != ARGUMENT_OFFLINE).ToArray();
    int portRPC = argsPort.Length > 0 ? int.Parse(argsPort[0]) : PORT_RPC_DEFAULT;

    Node node = new(new CommunicationTCP());

    if (!args.Contains(ARGUMENT_OFFLINE))
      await node.Start();

    await new RPCServer(node, portRPC).Start();
  }

  class WriterLog(TextWriter console, StreamWriter file) : TextWriter
  {
    public override Encoding Encoding
    {
      get { return console.Encoding; }
    }

    public override void Write(char value)
    {
      console.Write(value);
      file.Write(value);
    }

    public override void WriteLine(string value)
    {
      console.WriteLine(value);
      file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {value}");
    }
  }
}
