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
    Blockchain Blockchain;

    BitcoinGenesisBlock GenesisBlock = new BitcoinGenesisBlock();

    Dictionary<int, byte[]> Checkpoints = new Dictionary<int, byte[]>()
      {
        { 11111, "0000000069e244f73d78e8fd29ba2fd2ed618bd6fa2ee92559f542fdb26e7c1d".ToBinary() },
        { 250000, "000000000000003887df1f29024b06fc2200b55f8af8f35453d7be294df2d214".ToBinary() }
      };


    public Node()
    {
      Blockchain = new Blockchain(
        GenesisBlock.Header,
        GenesisBlock.BlockBytes,
        Checkpoints);
    }

    public void Start()
    {
      Blockchain.Start();
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
            Console.WriteLine(Blockchain.GetStatus());
            break;

          case "utxo":
            Console.WriteLine(Blockchain.UTXOTable.GetStatus());
            break;

          case "wallet":
            Console.WriteLine(
              Blockchain.UTXOTable.Wallet.GetStatus());
            break;

          case "sendtoken":
            Console.WriteLine(inputCommand);

            UTXOTable.TX tXAnchorToken = Blockchain.UTXOTable.Wallet.CreateAnchorToken(
              "AA55AA55AA55AA55AA55AA55AA55AA55AA55AA55AA55EE11EE11EE11EE11EE11EE11EE11EE11EE11".ToBinary());
            
            Blockchain.Network.SendTX(tXAnchorToken);
            break;

          default:
            Debug.WriteLine("Unknown command {0}", inputCommand);
            break;
        }
      }
    }
  }
}