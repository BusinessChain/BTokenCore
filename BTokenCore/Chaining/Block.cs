using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore.Chaining
{
  public class Block
  {
    public byte[] Buffer;

    public Header Header;

    public Block(
      byte[] buffer,
      Header header)
    {
      Buffer = buffer;
      Header = header;
    }
  }
}
