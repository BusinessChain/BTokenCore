using System;
using System.Collections.Generic;
using System.Net;

namespace BTokenLib
{
  partial class Network
  {
    class VersionMessage : NetworkMessage
    {
      public VersionMessage(
        uint protocolVersion,
        ulong networkServicesLocal,
        long unixTimeSeconds,
        ulong networkServicesRemote,
        IPAddress iPAddressRemote,
        ushort portRemote,
        IPAddress iPAddressLocal,
        ushort portLocal,
        ulong nonce,
        string userAgent,
        int blockchainHeight,
        byte relayOption) 
        : base("version")
      {
        List<byte> versionPayload = new();

        versionPayload.AddRange(BitConverter.GetBytes(protocolVersion));
        versionPayload.AddRange(BitConverter.GetBytes(networkServicesLocal));
        versionPayload.AddRange(BitConverter.GetBytes(unixTimeSeconds));
        versionPayload.AddRange(BitConverter.GetBytes(networkServicesRemote));
        versionPayload.AddRange(iPAddressRemote.GetAddressBytes());
        versionPayload.AddRange(GetBytes(portRemote));
        versionPayload.AddRange(BitConverter.GetBytes(networkServicesLocal));
        versionPayload.AddRange(iPAddressLocal.GetAddressBytes());
        versionPayload.AddRange(GetBytes(portLocal));
        versionPayload.AddRange(BitConverter.GetBytes(nonce));
        versionPayload.AddRange(VarString.GetBytes(userAgent));
        versionPayload.AddRange(BitConverter.GetBytes(blockchainHeight));
        versionPayload.Add(relayOption);

        Payload = versionPayload.ToArray();
        LengthPayload = Payload.Length;
      }

      byte[] GetBytes(UInt16 uint16)
      {
        byte[] byteArray = BitConverter.GetBytes(uint16);
        Array.Reverse(byteArray);
        return byteArray;
      }
    }
  }
}
