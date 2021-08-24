using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public class Block
  {
    public byte[] Buffer;
    public int StopIndex;

    public Header Header;

    public Block(
      byte[] buffer,
      int stopIndex,
      Header header)
    {
      Buffer = buffer;
      StopIndex = stopIndex;
      Header = header;
    }
  }
}
