using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;

using BTokenLib;

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
    }



    public void RunConsole()
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

          case "sendtoken":
            Console.WriteLine(inputCommand);

            TokenBitcoin.SendTX();
            break;

          default:
            Console.WriteLine("Unknown command {0}", inputCommand);
            break;
        }
      }
    }
  }
}