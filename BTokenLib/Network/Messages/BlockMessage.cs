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
      public BlockMessage(
        byte[] buffer, 
        int indexPayloadOffset, 
        int countBytesPayload)
        : base(
            "block", 
            buffer,
            indexPayloadOffset,
            countBytesPayload)
      { }
    }
  }
}