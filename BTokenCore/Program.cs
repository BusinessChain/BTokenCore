using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace BTokenCore
{
  class Program
  {
    static void Main(string[] args)
    {
      try
      {
        var node = new Node();
        node.Start();

        node.RunConsole();
      }
      catch(Exception ex)
      {
        Console.WriteLine(ex.Message);
      }

      Console.ReadKey();
    }
  }
}
