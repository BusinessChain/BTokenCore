using System.Net;
using System.Net.Sockets;

namespace BTokenCore;

public partial class CommunicationTCP : ICommunication
{
  TcpListener TcpListener;


  void ICommunication.StartListenerCommunicationInbound(int port)
  {
    TcpListener = new(IPAddress.Any, port);
    TcpListener.Start();
  }

  async Task<ISocketCommunication> ICommunication.AcceptSocketCommunicationInbound()
  {
    TcpClient tcpClient = await TcpListener.AcceptTcpClientAsync().ConfigureAwait(false);

    return new SocketTCP(tcpClient);
  }

  ISocketCommunication ICommunication.GetSocketCommunication(int port, string address)
  {
    return new SocketTCP(address, port);
  }
}
