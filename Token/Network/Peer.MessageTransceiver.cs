using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;


namespace BTokenCore;

public partial class Peer
{
  const int TIMEOUT_HANDSHAKE_MILLISECONDS = 5000;
  public StateProtocol StateCurrent = StateProtocol.Handshake;


  async Task StartMessageReceiver()
  {
    try
    {
      while (true)
      {
        MessageNetworkProtocol message =
          await SocketCommunication.ReceiveMessageNext();

        message.DOSMonitor.Increment(1);

        message.Run(this);
      }
    }
    catch
    {
      SocketCommunication.Dispose();
    }
  }

  public async Task SendMessage(MessageNetworkProtocol message)
  {
    await SocketCommunication.SendMessage(message.GetCommand(), message.LengthDataPayload, message.Payload);
  }
}