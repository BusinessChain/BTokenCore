using System;
using System.Linq;
using System.Text;


namespace BTokenCore;


public interface ISocketToken //wozu?
{
}

public interface ISocketCommunication
{
}

public interface IEnvironment
{
  public Task<ISocketCommunication> GetSocketCommunication(Token token, string address);
}