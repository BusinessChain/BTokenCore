using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;

namespace BTokenCore
{
  class Program
  {
    static void Main(string[] args)
    {
      NodeBToken node = new();

      Console.WriteLine("Start node.");
      node.Start();

      Console.ReadKey();
    }
  }
}
