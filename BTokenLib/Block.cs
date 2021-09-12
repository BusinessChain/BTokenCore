using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace BTokenLib
{
  public abstract class Block
  {
    public byte[] Buffer = new byte[0x400000];
    public int IndexBufferStop;

    public Header Header;


    public Block()
    { }

    public Block(Header header)
    {
      Header = header;
    }


    public abstract Header Parse();

    public abstract Block Parse(
      byte[] buffer,
      ref int startIndex);

    public abstract void Parse(Block block);

    public abstract byte[] GetBuffer(out int indexBufferStop);

    public abstract byte[] GetBuffer();
  }
}
