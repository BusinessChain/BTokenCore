using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using BTokenLib;

namespace BTokenCore
{
  class TokenAnchor : TX
  {
    public class Configuration
    {
      public List<TXOutputWallet> TXOutputWalletConsumed = new();
      public int CountAnchorTokens;
      public ulong ValueChange;
    }

    public byte[] HashBlock = new byte[32];
    public byte[] HashPrevious = new byte[32];


    public TokenAnchor(byte[] buffer, int index)
    {
      Array.Copy(buffer, index, HashBlock, 0, HashBlock.Length);
    }

    public TokenAnchor()
    {
    }
  }
}
