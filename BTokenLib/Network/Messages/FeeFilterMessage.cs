using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  class FeeFilterMessage : NetworkMessage
  {
    public ulong FeeFilterValue { get; private set; }

    public FeeFilterMessage(byte[] messagePayload) 
      : base("feefilter", messagePayload)
    { }
  }
}
