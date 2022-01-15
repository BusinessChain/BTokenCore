using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Linq;



namespace BTokenCore
{
  partial class Node
  {
    TokenBitcoin Bitcoin;

    TokenBToken BToken;

    

    public Node(string pathBlockArchive)
    {
      Bitcoin = new(pathBlockArchive);

      BToken = new TokenBToken(
        pathBlockArchive,
        Bitcoin);
    }

    public void Start()
    {
      Bitcoin.Start();

      RunConsole();
    }

    void RunConsole()
    {
      Console.WriteLine("Start console.");

      while (true)
      {
        string inputCommand = Console.ReadLine();

        switch(inputCommand)
        {
          case "status":
            Console.WriteLine(Bitcoin.GetStatus());
            break;

          case "statusNet":
            Console.WriteLine(Bitcoin.Network.GetStatus());
            break;

          case "startMiner":
            new Thread(Bitcoin.StartMiner).Start();
            break;

          case "stopMiner":
            Bitcoin.StopMiner();
            break;

          case "sendtoken":
            Bitcoin.SendTX();
            break;

          case "addPeer":
            Bitcoin.Network.AddPeer();
            break;

          case "addPeerIP":
            Bitcoin.Network.AddPeer("3.67.200.137"); // "84.75.2.239"
            break;

          case "sync":
            Bitcoin.Network.ScheduleSynchronization();
            break;

          case "removePeer":
            string iPRemove = Console.ReadLine();
            Bitcoin.Network.RemovePeer(iPRemove);
            break;

          default:
            Console.WriteLine($"Unknown command {inputCommand}.");
            break;
        }
      }
    }
  }
}