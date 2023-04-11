using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace BTokenCore
{
  class Program
  {
    static TokenBToken BToken;

    static void Main(string[] args)
    {
      Console.WriteLine("Start node.");

      BToken = new(
        new TokenBitcoin());

      BToken.Start();

      RunConsole();
    }


    static void RunConsole()
    {
      // Alle Kommandos sollten ein Objekt sein und über
      // einen Dictionär <string, Command> direkt selektiert werden.
      // Command command = DictCommands[inputCommand];
      // command. Excecute();
      //
      // Das help kommando listet alle Keys vom Dict auf.

      Console.WriteLine("Start console.");

      while (true)
      {
        string inputCommand = Console.ReadLine();

        switch (inputCommand)
        {
          case "status":
            Console.WriteLine(BToken.GetStatus());
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
            BToken.Network.AddPeer();
            break;

          case "sync":
            BToken.Network.ScheduleSynchronization();
            break;

          case "removePeer":
            string iPRemove = Console.ReadLine();
            BToken.Network.RemovePeer(iPRemove);
            break;

          default:
            Console.WriteLine($"Unknown command {inputCommand}.");
            break;
        }
      }
    }
  }
}
