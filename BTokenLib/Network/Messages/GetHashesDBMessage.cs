using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace BTokenLib
{
  partial class Network
  {
    class GetHashesDBMessage : NetworkMessage
    {
      public GetHashesDBMessage()
        : base("getHashesDB")
      { }
    }
  }
}