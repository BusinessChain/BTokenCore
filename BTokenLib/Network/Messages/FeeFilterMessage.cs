using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  partial class Network
  {
    class FeeFilterMessage : MessageNetwork
    {
      public ulong FeeFilterValue { get; private set; }

      public FeeFilterMessage(byte[] messagePayload)
        : base("feefilter", messagePayload)
      { }
    }
  }
}
