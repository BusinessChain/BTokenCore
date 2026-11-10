namespace BTokenCore;

internal partial class Network
{
  protected abstract class NetworkMessage
  {
    internal byte[] Payload;
    internal int LengthDataPayload;

    protected DOSMonitor DOSMonitor;


    internal NetworkMessage(byte[] payload, int maxLevelDoS, int amountDrainDoSPer10Minutes)
    {
      Payload = payload;
      LengthDataPayload = payload.Length;
      DOSMonitor = new DOSMonitor(maxLevelDoS, amountDrainDoSPer10Minutes);
    }

    internal virtual byte[] GetPayloadBuffer()
    {
      return Payload;
    }

    internal virtual void IncrementDOSMonitor()
    {
      DOSMonitor.Increment(1);
    }

    internal abstract Task Run(Peer peer);

    internal static async Task SendItems(Peer peer, string command, List<byte[]> items)
    {
      byte[] buffer = [.. VarInt.GetBytes(items.Count), .. items.SelectMany(i => i)];

      await peer.SocketCommunication.SendMessage(command, buffer.Length, buffer);
    }

    internal static async Task SendInventories(Peer peer, string command, List<Inventory> inventories)
    {
      await SendItems(peer, command, inventories.Select(i => i.GetBytes()).ToList());
    }
  }
}