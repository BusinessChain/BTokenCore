using System;
using System.IO;
using System.Threading;

using BTokenLib;

namespace BTokenCore
{
  class Program
  {
    static TokenBToken BToken;

    static void Main(string[] args)
    {
      Console.WriteLine("Start node.");

      BToken = new();

      BToken.Start();

      RunConsole();
    }


    static void RunConsole()
    {
      Console.WriteLine("Start console.");

      while (true)
      {
        string inputCommand = Console.ReadLine();

        switch (inputCommand)
        {
          case "status":
            Console.WriteLine(BToken.GetStatus());

            string textImage = "";
            BToken.PrintImage(ref textImage);
            File.WriteAllText("printImage.txt", textImage);

            string textBlocks = "";
            BToken.PrintBlocks(ref textBlocks);
            File.WriteAllText("printBlocks.txt", textBlocks);
            break;

          case "sync":
            BToken.TokenParent.Network.TryStartSynchronization();
            break;

          case "startBTokenMiner":
            BToken.StartMining();
            break;

          case "startBitcoinMiner":
            new Thread(BToken.TokenParent.StartMining).Start();
            break;

          case "stopMiner":
            BToken.StopMining();
            BToken.TokenParent.StopMining();
            break;

          case "sendtoken":
            //BToken.TokenParent.SendTX();
            break;

          case "addPeer":
            BToken.Network.IncrementCountMaxPeers();
            break;

          default:
            Console.WriteLine($"Unknown command {inputCommand}.");
            break;
        }
      }
    }
  }
}
