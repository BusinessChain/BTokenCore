using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;


namespace BTokenCore
{
  partial class NodeBToken
  {
    TokenBToken BToken;


    public NodeBToken()
    {
      BToken = new(
        new TokenBitcoin());
    }

    public void Start()
    {
      BToken.Start();

      RunConsole();
    }

    void RunConsole()
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

        switch(inputCommand)
        {
          case "status":

            Console.WriteLine(BToken.GetStatus());
            break;

          case "startBTokenMiner":
            new Thread(BToken.StartMining)
              .Start();
            break;

          case "startBitcoinMiner":
            new Thread(BToken.TokenParent.StartMining)
              .Start();
            break;

          case "stopBTokenMiner":
            BToken.StopMining();
            BToken.TokenParent.StopMining();
            break;

          case "sendtoken":
            //BToken.TokenParent.SendTX();
            break;

          case "addPeer":
            BToken.Network.AddPeer();
            break;

          case "addPeerIP":
            BToken.Network.AddPeer("3.67.200.137"); // "84.75.2.239"
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