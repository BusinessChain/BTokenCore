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
      class GetHeadersMessage : MessageNetworkProtocol
      {
        public const string Command = "getheaders";

        int HeightAncestorSentLast;


        public GetHeadersMessage()
        {
        }

        public override async Task Run(Peer peer)
        {
          int startIndex = 0;

          byte[] version = new byte[4];
          Array.Copy(Payload, startIndex, version, 0, version.Length);
          startIndex += version.Length;

          int countHeaderLocator = VarInt.GetInt(Payload, ref startIndex);

          if (countHeaderLocator > 101)
            throw new ProtocolException($"Too many ({countHeaderLocator}) headers in locator.");

          List<byte[]> hashesLocator = new();

          for (int i = 0; i < countHeaderLocator; i += 1)
          {
            byte[] hashLocator = new byte[32];
            Array.Copy(Payload, startIndex, hashLocator, 0, hashLocator.Length);
            startIndex += hashLocator.Length;

            hashesLocator.Add(hashLocator);
          }

          (List<byte[]> headers, int heightAncestor) tupleHeadersSerialized =
            await peer.Network.GetHeadersSerialized(
              hashesLocator,
              HeadersMessage.MAX_COUNT_HEADERS);

          HeadersMessage.SendHeaders(peer, tupleHeadersSerialized.headers);

          if (tupleHeadersSerialized.heightAncestor > HeightAncestorSentLast)
          {
            DOSMonitor.Decrement(1);
            HeightAncestorSentLast = tupleHeadersSerialized.heightAncestor;
          }
        }

        public static async Task SendGetHeaders(Peer peer, List<byte[]> locator)
        {
          List<byte> payload = new();

          payload.AddRange(BitConverter.GetBytes(peer.Network.ProtocolVersion));
          payload.AddRange(VarInt.GetBytes(locator.Count()));

          foreach (byte[] locatorHash in locator)
            payload.AddRange(locatorHash);

          payload.AddRange("0000000000000000000000000000000000000000000000000000000000000000".ToBinary());

          byte[] buffer = payload.ToArray();

          await peer.SendMessage(Command, buffer.Length, buffer);

          peer.Log($"Send getheaders. Locator: {locator.First().ToHexString()} ... {locator.Last().ToHexString()}");
        }

        public override string GetCommand()
        {
          return Command;
        }
      }
    }
  }
}