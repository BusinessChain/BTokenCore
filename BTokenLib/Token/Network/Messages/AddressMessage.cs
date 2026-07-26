using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      partial class Peer
      {
        class AddressMessage : MessageNetworkProtocol
        {
          const string Command = "addr";

          public List<NetworkAddress> NetworkAddresses = new();

          public AddressMessage()
          { }

          public AddressMessage(byte[] messagePayload)
            : base(messagePayload)
          {
            int startIndex = 0;

            int addressesCount = VarInt.GetInt(
              Payload,
              ref startIndex);

            for (int i = 0; i < addressesCount; i++)
            {
              NetworkAddress address = NetworkAddress.ParseAddress(
                  Payload, ref startIndex);

              if (NetworkAddresses.Any(
                a => a.IPAddress.ToString() == address.IPAddress.ToString()))
                throw new ProtocolException("Duplicate network address advertized.");

              NetworkAddresses.Add(address);
            }
          }


          public override async Task Run(Peer peer)
          {

          }

          public override string GetCommand()
          {
            return Command;
          }
        }
      }
    }
  }
}
