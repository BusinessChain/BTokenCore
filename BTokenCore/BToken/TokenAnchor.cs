using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore
{
  class TokenAnchor
  {
    public byte[] HashBlock = new byte[32];
    public byte[] HashDatabase = new byte[32];


    public TokenAnchor(byte[] buffer, int index)
    {
      Array.Copy(buffer, index, HashBlock, 0, HashBlock.Length);

      index += HashBlock.Length;

      Array.Copy(buffer, index, HashDatabase, 0, HashDatabase.Length);
    }
  }
}
