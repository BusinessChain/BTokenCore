using System.Net;


namespace BTokenCore;

internal partial class Network
{
  internal class Address
  {
    internal UInt32 UnixTimeSeconds { get; private set; }
    internal UInt64 NetworkServices { get; private set; }
    internal IPAddress IPAddress { get; private set; }
    internal UInt16 Port { get; private set; }


    internal Address(
      UInt32 unixTimeSeconds,
      UInt64 networkServices,
      IPAddress iPAddress,
      UInt16 port)
    {
      UnixTimeSeconds = unixTimeSeconds;
      NetworkServices = networkServices;
      IPAddress = iPAddress;
      Port = port;
    }

    internal static Address ParseAddress(byte[] buffer, ref int startIndex)
    {
      UInt32 unixTimeSeconds = BitConverter.ToUInt32(buffer, startIndex);
      startIndex += 4;

      UInt64 networkServices = BitConverter.ToUInt64(buffer, startIndex);
      startIndex += 8;

      IPAddress iPAddress = new IPAddress(buffer.ReadBytes(ref startIndex, 16));

      UInt16 port = BitConverter.ToUInt16(buffer, startIndex);
      startIndex += 2;

      return new Address(unixTimeSeconds, networkServices, iPAddress, port);
    }
  }
}