using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace BTokenLib
{
  public abstract class Block
  {
    public Header Header;

    public byte[] Buffer;


    public Block()
    { }

    public Block(Header header)
    {
      Header = header;
    }


    public void Parse()
    {
      int index = 0;
      Parse(Buffer, ref index);
    }

    public abstract void Parse(
      byte[] buffer,
      ref int index);

    public override string ToString()
    {
      return Header.ToString();
    }
  }
}
