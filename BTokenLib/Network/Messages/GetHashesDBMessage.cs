using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace BTokenLib
{
  partial class Network
  {
    class GetHashesDBMessage : MessageNetwork
    {
      public GetHashesDBMessage()
        : base("getHashesDB")
      { }
    }
  }
}