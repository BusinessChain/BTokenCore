using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;


namespace BTokenLib
{
  partial class Network
  {
    class MessageBlock : MessageNetwork
    {
      public MessageBlock(Block block)
        : base(
            "block",
            block.Buffer,
            0,
            block.Header.CountBytesBlock)
      { }
    }
  }
}