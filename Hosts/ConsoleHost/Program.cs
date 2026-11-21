using System.Text;
using BTokenCore;

namespace ConsoleHost;

class Program
{
  const int PORT_RPC_DEFAULT = 8778;
  const string NAME_FILE_LOG = "node.log";


  static async Task Main(string[] args)
  {
    Console.SetOut(new WriterLog(Console.Out, new StreamWriter(NAME_FILE_LOG, append: true) { AutoFlush = true }));

    int portRPC = args.Length > 0 ? int.Parse(args[0]) : PORT_RPC_DEFAULT;

    Node node = new(new CommunicationTCP());

    await node.Start(portRPC);
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
