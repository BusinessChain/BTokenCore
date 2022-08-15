using System;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text;
using System.Linq;


namespace BTokenLib
{
  partial class Network
  {
    public class MessageNetwork
    {
      public string Command;

      public byte[] Payload;
      public int OffsetPayload;
      public int LengthDataPayload;



      public MessageNetwork(string command)
        : this(
            command,
            new byte[0])
      { }

      public MessageNetwork(
        string command,
        byte[] payload)
        : this(
            command,
            payload,
            0,
            payload.Length)
      { }

      public MessageNetwork(
        string command,
        byte[] payload,
        int indexPayloadOffset,
        int lengthPayload)
      {
        OffsetPayload = indexPayloadOffset;
        LengthDataPayload = lengthPayload;

        Command = command;
        Payload = payload;
      }
    }
  }
}
