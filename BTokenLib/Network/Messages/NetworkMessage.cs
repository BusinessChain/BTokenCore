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
    public class NetworkMessage
    {
      public string Command;

      public byte[] Payload;
      public int OffsetPayload;
      public int LengthDataPayload;



      public NetworkMessage(string command)
        : this(
            command,
            new byte[0])
      { }

      public NetworkMessage(
        string command,
        byte[] payload)
        : this(
            command,
            payload,
            0,
            payload.Length)
      { }

      public NetworkMessage(
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
