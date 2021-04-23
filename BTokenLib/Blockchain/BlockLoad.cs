using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  partial class Blockchain
  {
    class BlockLoad
    {
      public int Index;
      public List<Block> Blocks = new();
      public int CountBytes;

      public bool IsInvalid;


      public void InsertBlock(Block block)
      {
        if (
          Blocks.Any() &&
          !block.Header.HashPrevious.IsEqual(
            Blocks.Last().Header.Hash))
        {
          throw new ProtocolException(
            string.Format(
              "Headerchain out of order in blockArchive {0}.",
              Index));
        }

        Blocks.Add(block);
        CountBytes += block.Buffer.Length;
      }
    }
  }
}
