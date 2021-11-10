using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Linq;

namespace BTokenCore
{
  partial class Node
  {
    TokenBitcoin TokenBitcoin;

    //TokenBToken BToken;

    

    public Node(string pathBlockArchive)
    {
      TokenBitcoin = new TokenBitcoin(pathBlockArchive);

      //BToken = new TokenBitcoin(
      //  "configurationNetworkBToken",
      //  TokenBitcoin);
    }

    public void Start()
    {
      TokenBitcoin.Start();

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
            Console.WriteLine(TokenBitcoin.GetStatus());
            break;

          case "statusNet":
            Console.WriteLine(TokenBitcoin.Network.GetStatus());
            break;

          case "startMiner":
            new Thread(TokenBitcoin.StartMiner).Start();
            break;

          case "stopMiner":
            TokenBitcoin.StopMiner();
            break;

          case "sendtoken":
            TokenBitcoin.SendTX();
            break;

          case "addPeer":
            TokenBitcoin.Network.AddPeer();
            break;

          case "addPeerIP":
            string iPAddPeer = Console.ReadLine();
            TokenBitcoin.Network.AddPeer(iPAddPeer);
            break;

          case "removePeer":
            string iPRemove = Console.ReadLine();
            TokenBitcoin.Network.RemovePeer(iPRemove);
            break;

          default:
            Console.WriteLine("Unknown command {0}", inputCommand);
            break;
        }
      }
    }
  }
}