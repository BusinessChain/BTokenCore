using System;
using System.Linq;
using System.Text;


namespace BTokenCore;

public interface IPeer
{
  public bool IsDisposed();
}

public interface ISocketToken
{
  public void Log(string message);

  public Task<IPeer> GetInterfacePeer();
  public Task StartPeerInboundConnector();
}
