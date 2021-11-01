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

      Token Token;


      public BlockLoad(Token token)
      {
        Token = token;
      }

      public void Parse(byte[] buffer)
      {
        int startIndex = 0;

        while (startIndex < buffer.Length)
        {
          Block block = Token.CreateBlock();

          block.Parse(buffer, ref startIndex);

          InsertBlock(block);
        }

        CountBytes = buffer.Length;
        IsInvalid = false;
      }

      void InsertBlock(Block block)
      {
        if (
          Blocks.Any() &&
          !block.Header.HashPrevious.IsEqual(
            Blocks.Last().Header.Hash))
        {
          throw new ProtocolException(
            $"Headerchain out of order in blockArchive {Index}.");
        }

        Blocks.Add(block);
        CountBytes += block.Buffer.Length;
      }

      public void Initialize(int index)
      {
        Index = index;
        Blocks.Clear();
        CountBytes = 0;

        IsInvalid = false;
      }
    }
  }
}
