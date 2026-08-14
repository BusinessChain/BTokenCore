using System;


namespace BTokenCore;

public interface ISocketCommunication
{
  public Task Start();
  public Task SendMessage(string commandString, int lengthDataPayload, byte[] payload);
  public Task<string> ReceiveCommandMessageNext();
  public Task LoadMessageNext(MessageNetworkProtocol message);
  public void Dispose();
  public string GetIP();
}

public interface IEnvironment
{
  public void StartListenerCommunicationInbound(int port);

  public Task<ISocketCommunication> AcceptSocketCommunicationInbound();

  public ISocketCommunication GetSocketCommunication(Token token, string address);
}