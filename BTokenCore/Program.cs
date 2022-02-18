﻿using System;
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

        NodeBToken node = new();

        Console.WriteLine("Start node.");
        node.Start();
      }
      catch(Exception ex)
      {
        Console.WriteLine(
          $"Program aborted with {ex.GetType().Name}:\n" +
          $"{ex.Message}");
      }

      Console.ReadKey();
    }
  }
}
