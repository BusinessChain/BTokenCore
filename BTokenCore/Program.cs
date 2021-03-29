using System;
using System.Threading.Tasks;

namespace BTokenCore
{
  class Program
  {
    public static Node Node;

    static async Task Main(string[] args)
    {
      Node = new Node();
      Node.Start();

      await Task.Delay(-1).ConfigureAwait(false);
    }
  }
}
