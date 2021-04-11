using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;


namespace BTokenCore.Chaining
{
  class HeadersMessage : NetworkMessage
  {
    public List<Blockchain.Header> Headers = new List<Blockchain.Header>();


    public HeadersMessage(
      List<Blockchain.Header> headers)
      : base("headers")
    {
      Headers = headers;
      SerializePayload();
    }
    void SerializePayload()
    {
      var payload = new List<byte>();

      payload.AddRange(VarInt.GetBytes(Headers.Count));

      foreach (Blockchain.Header header in Headers)
      {
        payload.AddRange(header.GetBytes());
        payload.Add(0);
      }

      Payload = payload.ToArray();
    }
  }
}