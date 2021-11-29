using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public class Miner
  {
    Token Token;
    Network Network;
    Blockchain Blockchain;

    internal Miner(
      Blockchain blockchain,
      Network network)
    {
      Blockchain = blockchain;
      Network = network;
    }

    internal async Task Start()
    { }
  }
}
