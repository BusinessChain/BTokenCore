using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      partial class Peer
      {
        const int TIMEOUT_HANDSHAKE_MILLISECONDS = 5000;
        public StateProtocol StateCurrent = StateProtocol.Handshake;

        public const int CommandSize = 12;
        public const int ChecksumSize = 4;

        static readonly byte[] MagicBytes = new byte[4] { 0xF9, 0xBE, 0xB4, 0xD9 };
        byte[] MagicBytesRead = new byte[4];
        byte[] CommandRead = new byte[CommandSize];
        byte[] LengthRead = new byte[4];
        byte[] ChecksumRead = new byte[ChecksumSize];

        SemaphoreSlim SemaphoreSendMessage = new(1);

        DOSMonitorPer10Minutes DOSMonitor;


        async Task StartMessageReceiver()
        {
          while (true)
          {
            try
            {
              MessageNetworkProtocol message = await ReceiveMessageNext();

              message.DOSMonitor.Increment(1);

              message.Run(this);
            }
            catch (Exception ex)
            {
              Log(
                $"{ex.GetType().Name} in message receiver: " +
                $"\n{ex.Message}");

              if (DOSMonitor.IsOverflow)
                break;
            }
          }

          Log($"Disconnect from peer {this}");
          Dispose();
        }

        async Task<MessageNetworkProtocol> ReceiveMessageNext()
        {
          await ReadBytes(MagicBytesRead, 4);

          await ReadBytes(CommandRead, CommandRead.Length);
          string commandString = Encoding.ASCII.GetString(CommandRead).TrimEnd('\0');

          MessageNetworkProtocol message = ProtocolStateMachine[commandString];

          await ReadBytes(LengthRead, LengthRead.Length);
          message.LengthDataPayload = BitConverter.ToInt32(LengthRead);

          await ReadBytes(ChecksumRead, 4);

          byte[] bufferPayloadMessage = message.GetPayloadBuffer();

          await ReadBytes(bufferPayloadMessage, message.LengthDataPayload);

          return message;
        }

        async Task ReadBytes(byte[] buffer, int bytesToRead)
        {
          int offset = 0;

          try
          {
            while (bytesToRead > 0)
            {
              int chunkSize = await NetworkStream.ReadAsync(
                buffer,
                offset,
                bytesToRead,
                Cancellation.Token)
                .ConfigureAwait(false);

              if (chunkSize == 0)
                throw new IOException("Stream returns 0 bytes signifying end of stream.");

              offset += chunkSize;
              bytesToRead -= chunkSize;
            }
          }
          catch (OperationCanceledException)
          {
            Log($"Timeout occured when waiting for next message.");
            throw;
          }
        }

        async Task SendMessage(MessageNetworkProtocol message)
        {
          await SendMessage(message.GetCommand(), message.LengthDataPayload, message.Payload);
        }

        public async Task SendMessage(string commandString, int lengthDataPayload, byte[] payload)
        {
          await SemaphoreSendMessage.WaitAsync().ConfigureAwait(false);

          try
          {
            NetworkStream.Write(MagicBytes, 0, MagicBytes.Length);

            byte[] command = Encoding.ASCII.GetBytes(commandString.PadRight(CommandSize, '\0'));
            NetworkStream.Write(command, 0, command.Length);

            byte[] payloadLength = BitConverter.GetBytes(lengthDataPayload);
            NetworkStream.Write(payloadLength, 0, payloadLength.Length);

            byte[] checksum = SHA256.ComputeHash(
              SHA256.ComputeHash(payload, 0, lengthDataPayload));

            NetworkStream.Write(checksum, 0, ChecksumSize);

            NetworkStream.Write(payload, 0, lengthDataPayload);
          }
          finally
          {
            SemaphoreSendMessage.Release();
          }
        }

        void SetTimer(string descriptionTimeOut = "", int millisecondsTimer = int.MaxValue)
        {
          if (descriptionTimeOut != "")
            Log($"Set timeout for '{descriptionTimeOut}' to {millisecondsTimer} ms.");

          Cancellation.CancelAfter(millisecondsTimer);
        }
      }
    }
  }
}