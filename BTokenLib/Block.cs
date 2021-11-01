using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace BTokenLib
{
  public abstract class Block
  {
    public byte[] Buffer;
    public int IndexBufferStop;

    public Header Header;


    public Block()
    { }

    public Block(Header header)
    {
      Header = header;
    }


    public abstract void Parse();

    public abstract void Parse(
      byte[] buffer,
      ref int startIndex);

    public override string ToString()
    {
      return Header.ToString();
    }
  }
}
