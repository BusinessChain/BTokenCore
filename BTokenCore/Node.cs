using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;

using BTokenCore.Chaining;


namespace BTokenCore
{
  partial class Node
  {
    Blockchain Bitcoin;
    TokenBitcoin TokenBitcoin;

    Blockchain BToken;


    public Node()
    {
      TokenBitcoin = new TokenBitcoin();

      Bitcoin = new Blockchain(
        TokenBitcoin,
        "configurationNetworkBitcoin");


      //BToken = new Blockchain(
      //  new TokenBToken(),
      //  "configurationNetworkBCash");
    }

    public void Start()
    {
      Bitcoin.Start();
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
            Console.WriteLine(Bitcoin.GetStatus());
            break;

          case "sendtoken":
            Console.WriteLine(inputCommand);

            UTXOTable.TX tXAnchorToken =
              TokenBitcoin.UTXOTable.Wallet.CreateAnchorToken(
              "BB66AA55AA55AA55AA55AA55AA55AA55AA55AA55AA55EE11EE11EE11EE11EE11EE11EE11EE11EE11".ToBinary());
            
            Bitcoin.Network.SendTX(tXAnchorToken);
            break;

          default:
            Debug.WriteLine("Unknown command {0}", inputCommand);
            break;
        }
      }
    }
  }
}