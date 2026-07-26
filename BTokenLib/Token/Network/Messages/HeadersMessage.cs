using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      class HeadersMessage : MessageNetworkProtocol
      {
        public const string Command = "headers";

        public const int MAX_COUNT_HEADERS = 2000;

        Block BlockDownload;

        SHA256 SHA256 = SHA256.Create();


        public HeadersMessage(Block blockDownload)
        {
          BlockDownload = blockDownload;
          DOSMonitor = new DOSMonitorPer10Minutes(maxLevel: 5);
        }

        public override async Task Run(Peer peer)
        {
          int startIndex = 0;
          int countHeaders = VarInt.GetInt(Payload, ref startIndex);

          if (countHeaders > MAX_COUNT_HEADERS)
            throw new ProtocolException($"Too many headers {countHeaders} in headers message.");
          else if (countHeaders > 0)
          {
            Header headerRoot = ParseHeaderchain(peer, countHeaders, ref startIndex);

            List<byte[]> headerslocator = await peer.Network.ExtendHeaderchain(
              headerRoot,
              BlockDownload);

            if (headerslocator != null)
            {
              DOSMonitor.Decrement(1);
              GetHeadersMessage.SendGetHeaders(peer, headerslocator);
            }
          }
          else if (countHeaders == 0 && BlockDownload.Header != null)
            GetDataMessage.SendBlockRequest(peer, BlockDownload.Header.Hash);
        }

        Header ParseHeaderchain(Peer peer, int countHeaders, ref int startIndex)
        {
          Header headerRoot = peer.Network.Token.ParseHeader(Payload, ref startIndex, SHA256);
          VarInt.GetInt(Payload, ref startIndex);

          Header headerTip = headerRoot;

          countHeaders -= 1;

          while (countHeaders > 0)
          {
            Header header = peer.Network.Token.ParseHeader(Payload, ref startIndex, SHA256);
            VarInt.GetInt(Payload, ref startIndex);

            header.AppendToHeader(headerTip);
            headerTip.HeaderNext = header;
            headerTip = header;

            countHeaders -= 1;
          }

          return headerRoot;
        }

        public static async Task SendHeaders(Peer peer, List<byte[]> headersSerialized)
        {
          List<byte> bufferList = new();

          foreach (byte[] headerSerialized in headersSerialized)
          {
            bufferList.AddRange(headerSerialized);
            bufferList.Add(0x00);
          }

          bufferList.InsertRange(0, VarInt.GetBytes(bufferList.Count));

          byte[] buffer = bufferList.ToArray();

          await peer.SendMessage(Command, buffer.Length, buffer);
        }

        public override string GetCommand()
        {
          return Command;
        }
      }
    }
  }
}