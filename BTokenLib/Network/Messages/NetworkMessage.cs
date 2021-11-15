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
      public int LengthPayload;

      byte[] MagicBytes = new byte[4] { 0xF9, 0xBE, 0xB4, 0xD9 };
      const int CommandSize = 12;
      const int LengthSize = 4;
      const int ChecksumSize = 4;
      const int HeaderSize = CommandSize + LengthSize + ChecksumSize;
      byte[] MeassageHeader = new byte[HeaderSize];

      const int SIZE_MESSAGE_PAYLOAD_BUFFER = 0x400000;
      int PayloadLength;



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
        LengthPayload = lengthPayload;

        Command = command;
        Payload = payload;
      }

      public async Task<NetworkMessage> ReadMessage(
        NetworkStream stream)
      {
        for (int i = 0; i < MagicBytes.Length; i++)
        {
          byte magicByte = (byte)stream.ReadByte();

          if (MagicBytes[i] != magicByte)
            i = magicByte == MagicBytes[0] ? 0 : -1;
        }

        stream.Read(
          MeassageHeader, 
          0, 
          MeassageHeader.Length);

        Command = Encoding.ASCII.GetString(
          MeassageHeader.Take(CommandSize)
          .ToArray()).TrimEnd('\0');

        PayloadLength = BitConverter.ToInt32(
          MeassageHeader,
          CommandSize);

        if (PayloadLength > SIZE_MESSAGE_PAYLOAD_BUFFER)
          throw new ProtocolException(
            $"Message payload too big exceeding " +
            $"{SIZE_MESSAGE_PAYLOAD_BUFFER} bytes.");

        return null;
      }

      public async Task SendMessage(
        NetworkStream stream,
        SHA256 sHA256)
      {
        stream.Write(MagicBytes, 0, MagicBytes.Length);

        byte[] command = Encoding.ASCII.GetBytes(
          Command.PadRight(CommandSize, '\0'));

        stream.Write(command, 0, command.Length);

        byte[] payloadLength = BitConverter.GetBytes(LengthPayload);
        stream.Write(payloadLength, 0, payloadLength.Length);

        byte[] checksum = sHA256.ComputeHash(
          sHA256.ComputeHash(
            Payload,
            OffsetPayload,
            LengthPayload));

        stream.Write(checksum, 0, ChecksumSize);

        await stream.WriteAsync(
          Payload,
          OffsetPayload,
          LengthPayload)
          .ConfigureAwait(false);
      }
    }
  }
}
