namespace BTokenCore;

internal interface ISocketCommunication
{
  internal Task Start();
  internal Task SendMessage(string commandString, int lengthDataPayload, byte[] payload);
  internal Task<string> ReceiveCommandMessageNext();
  internal Task LoadMessageNext(MessageNetworkProtocol message);
  internal void Dispose();
  internal string GetIP();
}

public interface ICommunication
{
  internal void StartListenerCommunicationInbound(int port);

  internal Task<ISocketCommunication> AcceptSocketCommunicationInbound();

  internal ISocketCommunication GetSocketCommunication(Token token, string address);
}