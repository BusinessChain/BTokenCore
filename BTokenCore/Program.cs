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
      try
      {
        string pathConfigNode = "configNode";
        string pathBlockArchive = null;

        try
        {
          pathBlockArchive = File.ReadAllText(pathConfigNode);
        }
        catch
        {
          Console.WriteLine("Enter file path of block archive.");
          pathBlockArchive = Console.ReadLine();

          File.WriteAllText(pathConfigNode, pathBlockArchive);
        }

        var node = new Node(pathBlockArchive);

        Console.WriteLine("Start node.");
        node.Start();
      }
      catch(Exception ex)
      {
        Console.WriteLine(ex.Message);
      }

      Console.ReadKey();
    }
  }
}
