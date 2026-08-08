using System;
using System.Linq;
using System.Text;


namespace BTokenCore;

public interface ISocketCommunication
{
  public Task<Stream> Start();
  public void Dispose();
  public string GetIP();
}

public interface IEnvironment
{
  public void StartListenerCommunicationInbound(int port);

  public Task<ISocketCommunication> AcceptSocketCommunicationInbound();

  public Task<ISocketCommunication> GetSocketCommunication(Token token, string address);
}