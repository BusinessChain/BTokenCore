using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;


namespace BTokenCore;

public class SocketTCP : ISocketCommunication
{
  TcpClient TcpClient;
  Stream NetworkStream;
  string IP;
  int Port;

  public const int CommandSize = 12;
  public const int ChecksumSize = 4;

  static readonly byte[] MagicBytes = [0xF9, 0xBE, 0xB4, 0xD9];
  byte[] MagicBytesRead = new byte[4];
  byte[] CommandRead = new byte[CommandSize];
  byte[] LengthRead = new byte[4];
  byte[] ChecksumRead = new byte[ChecksumSize];

  SemaphoreSlim SemaphoreSendMessage = new(1);

  SHA256 SHA256 = SHA256.Create();

  CancellationTokenSource Cancellation = new();


  public SocketTCP(string iPAddress, int port)
  {
    TcpClient = new TcpClient();
    IP = iPAddress;
    Port = port;
  }

  public SocketTCP(TcpClient tcpClient)
  {
    TcpClient = tcpClient;

    IPEndPoint iPEndPoint = TcpClient.Client.RemoteEndPoint as IPEndPoint;

    IP = iPEndPoint.Address.ToString();
    Port = iPEndPoint.Port;
  }

  public async Task Start()
  {
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

  public async Task<string> ReceiveCommandMessageNext()
  {
    await ReadBytes(MagicBytesRead, 4);
    await ReadBytes(CommandRead, CommandRead.Length);

    return Encoding.ASCII.GetString(CommandRead).TrimEnd('\0');
  }

  public async Task LoadMessageNext(MessageNetworkProtocol message)
  {
    await ReadBytes(LengthRead, LengthRead.Length);
    message.LengthDataPayload = BitConverter.ToInt32(LengthRead);

    await ReadBytes(ChecksumRead, 4);

    byte[] bufferPayloadMessage = message.GetPayloadBuffer();

    await ReadBytes(bufferPayloadMessage, message.LengthDataPayload);
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

  public void Dispose()
  {
    TcpClient.Dispose();
  }

  public string GetIP()
  {
    return IP;
  }
}
