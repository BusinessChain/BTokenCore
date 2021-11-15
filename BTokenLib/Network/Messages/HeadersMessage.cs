using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;


namespace BTokenLib
{
  partial class Network
  {
    class HeadersMessage : NetworkMessage
    {
      public List<Header> Headers = new();


      public HeadersMessage(List<Header> headers)
        : base("headers")
      {
        Headers = headers;

        var payload = new List<byte>();

        payload.AddRange(VarInt.GetBytes(Headers.Count));

        foreach (Header header in Headers)
        {
          payload.AddRange(header.GetBytes());
          payload.Add(0);
        }

        Payload = payload.ToArray();
        LengthPayload = Payload.Length;
      }
    }
  }
}