using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;


namespace BTokenLib
{
  partial class Network
  {
    class MessageDB : MessageNetwork
    {
      public MessageDB(byte[] dataDB)
        : base(
            "dataDB",
            dataDB)
      { }
    }
  }
}