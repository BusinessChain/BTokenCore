using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

using BTokenLib;

namespace BTokenCore
{
  partial class NodeBToken
  {
    TokenBToken BToken;
    Network Network;


    public NodeBToken(string pathBlockArchive)
    {
      BToken = new(
        pathBlockArchive,
        new TokenBitcoin(pathBlockArchive));

      Network = new(BToken);

      BToken.ConnectNetwork(Network);
    }

    public void Start()
    {
      Network.Start();

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

          case "statusNet":
            Console.WriteLine(Network.GetStatus());
            break;

          case "startBTokenMiner":
            new Thread(BToken.StartMining)
              .Start(Network);
            break;

          case "startBitcoinMiner":
            new Thread(BToken.TokenParent.StartMining)
              .Start(Network);
            break;

          case "stopBTokenMiner":
            BToken.StopMining();
            BToken.TokenParent.StopMining();
            break;

          case "sendtoken":
            //BToken.TokenParent.SendTX();
            break;

          case "addPeer":
            Network.AddPeer();
            break;

          case "addPeerIP":
            Network.AddPeer("3.67.200.137"); // "84.75.2.239"
            break;

          case "sync":
            Network.ScheduleSynchronization();
            break;

          case "removePeer":
            string iPRemove = Console.ReadLine();
            Network.RemovePeer(iPRemove);
            break;

          default:
            Console.WriteLine($"Unknown command {inputCommand}.");
            break;
        }
      }
    }
  }
}