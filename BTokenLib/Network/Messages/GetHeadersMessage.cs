using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace BTokenLib
{
  partial class Network
  {
    class GetHeadersMessage : NetworkMessage
    {
      public GetHeadersMessage(
        List<Header> headerLocator,
        uint versionProtocol)
        : base("getheaders")
      {
        List<byte> payload = new();

        payload.AddRange(BitConverter.GetBytes(versionProtocol));
        payload.AddRange(VarInt.GetBytes(headerLocator.Count()));

        for (int i = 0; i < headerLocator.Count(); i++)
          payload.AddRange(headerLocator.ElementAt(i).Hash);

        payload.AddRange((
          "00000000000000000000000000000000" +
          "00000000000000000000000000000000").ToBinary());

        Payload = payload.ToArray();
        LengthPayload = Payload.Length;
      }
    }
  }
}