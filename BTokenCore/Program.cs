using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace BTokenCore
{
  class Program
  {
    static void Main(string[] args)
    {
      var node = new Node();
      node.Start();

      node.RunConsole();
    }
  }
}
