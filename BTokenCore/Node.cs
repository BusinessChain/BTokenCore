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

    Blockchain BCash;


    public Node()
    {
      var bitcoinGenesisBlock = new GenesisBlockBitcoin();

      var bitcoinCheckpoints = new Dictionary<int, byte[]>()
      {
        { 11111, "0000000069e244f73d78e8fd29ba2fd2ed618bd6fa2ee92559f542fdb26e7c1d".ToBinary() },
        { 250000, "000000000000003887df1f29024b06fc2200b55f8af8f35453d7be294df2d214".ToBinary() }
      };

      Bitcoin = new Blockchain(
        bitcoinGenesisBlock.Header,
        bitcoinGenesisBlock.BlockBytes,
        bitcoinCheckpoints);

      var bCashGenesisBlock = new GenesisBlockBCash();
      var bCashCheckpoints = new Dictionary<int, byte[]>() { };

      BCash = new Blockchain(
        bCashGenesisBlock.Header,
        bCashGenesisBlock.BlockBytes,
        bCashCheckpoints);
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
          case "blockchain":
            Console.WriteLine(Bitcoin.GetStatus());
            break;

          case "utxo":
            Console.WriteLine(Bitcoin.UTXOTable.GetStatus());
            break;

          case "wallet":
            Console.WriteLine(
              Bitcoin.UTXOTable.Wallet.GetStatus());
            break;

          case "sendtoken":
            Console.WriteLine(inputCommand);

            UTXOTable.TX tXAnchorToken = Bitcoin.UTXOTable.Wallet.CreateAnchorToken(
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