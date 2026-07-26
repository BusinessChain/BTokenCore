using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      abstract class MessageNetworkProtocol
      {
        public byte[] Payload;
        public int LengthDataPayload;

        public DOSMonitorPer10Minutes DOSMonitor;


        public MessageNetworkProtocol()
          : this(new byte[0])
        { }

        public MessageNetworkProtocol(byte[] payload)
        {

          Payload = payload;

          LengthDataPayload = payload.Length;
        }

        public virtual byte[] GetPayloadBuffer()
        {
          return Payload;
        }

        public abstract Task Run(Peer peer);

        public abstract string GetCommand();
      }
    }
  }
}
