using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;


namespace BTokenCore;

internal class SocketTCP : ISocketCommunication
{
  TcpClient TcpClient;
  Stream NetworkStream;
  string IP;
  int Port;

  internal const int SIZE_COMMAND = 12;
  internal const int SIZE_CHECKSUM = 4;

  byte[] MagicBytes;
  byte[] MagicBytesRead = new byte[4];
  byte[] CommandRead = new byte[SIZE_COMMAND];
  byte[] LengthRead = new byte[4];
  byte[] ChecksumRead = new byte[SIZE_CHECKSUM];

  SemaphoreSlim SemaphoreSendMessage = new(1);

  CancellationTokenSource Cancellation = new();


  internal SocketTCP(string iPAddress, int port)
  {
    TcpClient = new TcpClient();
    IP = iPAddress;
    Port = port;
  }

  internal SocketTCP(TcpClient tcpClient)
  {
    TcpClient = tcpClient;

    IPEndPoint iPEndPoint = TcpClient.Client.RemoteEndPoint as IPEndPoint;

    IP = iPEndPoint.Address.ToString();
    Port = iPEndPoint.Port;
  }

  public async Task Start(byte[] magicBytes)
  {
    MagicBytes = magicBytes;

    if (!TcpClient.Connected)
      await TcpClient.ConnectAsync(IP, Port).ConfigureAwait(false);

    NetworkStream = TcpClient.GetStream();
  }

  public async Task SendMessage(string commandString, int lengthDataPayload, byte[] payload)
  {
    await SemaphoreSendMessage.WaitAsync().ConfigureAwait(false);

    try
    {
      NetworkStream.Write(MagicBytes, 0, MagicBytes.Length);

      byte[] command = Encoding.ASCII.GetBytes(commandString.PadRight(SIZE_COMMAND, '\0'));
      NetworkStream.Write(command, 0, command.Length);

      byte[] payloadLength = BitConverter.GetBytes(lengthDataPayload);
      NetworkStream.Write(payloadLength, 0, payloadLength.Length);

      byte[] checksum = Crypto.ComputeHash256(payload.AsSpan(0, lengthDataPayload));

      NetworkStream.Write(checksum, 0, SIZE_CHECKSUM);

      NetworkStream.Write(payload, 0, lengthDataPayload);
    }
    finally
    {
      SemaphoreSendMessage.Release();
    }
  }

  public async Task<string> ReceiveCommandMessageNext()
  {
    await ReadBytes(MagicBytesRead, 4);

    if (!MagicBytesRead.IsAllBytesEqual(MagicBytes))
      throw new ProtocolException($"Magic bytes {MagicBytesRead.ToHexString()} do not match {MagicBytes.ToHexString()}.");

    await ReadBytes(CommandRead, CommandRead.Length);

    return Encoding.ASCII.GetString(CommandRead).TrimEnd('\0');
  }

  public async Task<int> ReceivePayloadNext(byte[] buffer)
  {
    await ReadBytes(LengthRead, LengthRead.Length);
    int lengthDataPayload = BitConverter.ToInt32(LengthRead);

    await ReadBytes(ChecksumRead, 4);

    if (lengthDataPayload < 0 || lengthDataPayload > buffer.Length)
      throw new ProtocolException(
        $"Payload length {lengthDataPayload} exceeds buffer size {buffer.Length}.");

    await ReadBytes(buffer, lengthDataPayload);

    return lengthDataPayload;
  }

  public void Dispose()
  {
    TcpClient.Dispose();
  }

  public string GetIP()
  {
    return IP;
  }


  async Task ReadBytes(byte[] buffer, int bytesToRead)
  {
    int offset = 0;

    while (bytesToRead > 0)
    {
      int chunkSize = await NetworkStream.ReadAsync(
        buffer,
        offset,
        bytesToRead,
        Cancellation.Token).ConfigureAwait(false);

      if (chunkSize == 0)
        throw new IOException("Stream returns 0 bytes signifying end of stream.");

      offset += chunkSize;
      bytesToRead -= chunkSize;
    }
  }
}
