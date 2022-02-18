using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;


namespace BTokenLib
{
  partial class Network
  {
    class BlockMessage : NetworkMessage
    {
      public BlockMessage(Block block)
        : base(
            "block",
            block.Buffer,
            block.Header.StartIndexBlockArchive,
            block.Header.CountBlockBytes)
      { }
    }
  }
}