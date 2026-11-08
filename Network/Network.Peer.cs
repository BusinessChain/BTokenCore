namespace BTokenCore;

internal partial class Network
{
  protected abstract class Peer
  {
    const int TIMESPAN_HEARTBEAT_MILLISECONDS = 1000;
    static readonly int[] SECONDS_DELAY_RETRY_REFUSED = [10, 30, 60, 180];

    internal Dictionary<string, NetworkMessage> MessagesByCommand;

    internal Network Network;

    internal ISocketCommunication SocketCommunication;

    internal enum ConnectionType { OUTBOUND, INBOUND };
    internal ConnectionType Connection;

    internal enum StateProtocol
    {
      Handshake,
      Idle,
      HeaderDownload,
      BlockDownload,
      AccountsDownload,
      Disposed
    }

    internal StateProtocol StateCurrent = StateProtocol.Handshake;

    internal SemaphoreSlim SemaphorePeer = new(1);

    internal HashSet<byte[]> HashesSegmentRefused = new(new EqualityComparerByteArray());

    internal Dictionary<byte[], (int countRefusals, DateTime timeRefusedLast)> HashesBlockRefused = new(new EqualityComparerByteArray());

    internal TaskCompletionSource HandshakeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    HashSet<byte[]> HashesTXOwnedAnnounced = new(new EqualityComparerByteArray());


    protected Peer(
      Network network,
      ISocketCommunication socketCommunication,
      ConnectionType connection)
    {
      Network = network;

      MessagesByCommand = new()
      {
        { VersionMessage.Command, new VersionMessage() },
        { PingMessage.Command, new PingMessage() },
        { PongMessage.Command, new PongMessage() },
        { AddressMessage.Command, new AddressMessage() },
        { GetAddrMessage.Command, new GetAddrMessage() },
        { UnknownMessage.Command, new UnknownMessage() }
      };

      SocketCommunication = socketCommunication;
      Connection = connection;
    }

    internal bool IsDisposed()
    {
      return StateCurrent == StateProtocol.Disposed;
    }

    internal virtual bool CanDeliverBlock(Header header)
    {
      if (!HashesBlockRefused.TryGetValue(header.Hash, out (int countRefusals, DateTime timeRefusedLast) refusal))
        return true;

      int secondsDelay = SECONDS_DELAY_RETRY_REFUSED[Math.Min(refusal.countRefusals, SECONDS_DELAY_RETRY_REFUSED.Length) - 1];

      return DateTime.UtcNow - refusal.timeRefusedLast >= TimeSpan.FromSeconds(secondsDelay);
    }

    protected virtual async Task RelayTXs()
    {
      List<byte[]> hashesTXOwnedUnconfirmed = await Network.Blockchain.RunLocked(Network.Blockchain.GetHashesTXOwnedUnconfirmed);

      List<Inventory> inventoriesTXOwnedNew = hashesTXOwnedUnconfirmed
        .Where(h => !HashesTXOwnedAnnounced.Contains(h))
        .Select(h => new Inventory(Inventory.InventoryType.MSG_TX, h))
        .ToList();

      if (inventoriesTXOwnedNew.Count > 0)
        await NetworkMessage.SendInventories(this, InvMessage.Command, inventoriesTXOwnedNew);

      HashesTXOwnedAnnounced = new(hashesTXOwnedUnconfirmed, new EqualityComparerByteArray());
    }

    internal async Task Start(int heightBlockchainTip)
    {
      await SocketCommunication.Start(Network.ConfigurationNetwork.MagicBytes);

      StartMessageReceiver();
      StartHeartbeat();

      if (Connection == ConnectionType.OUTBOUND)
        await VersionMessage.SendVersion(this, heightBlockchainTip);

      await HandshakeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(TIMEOUT_REQUEST_SECONDS));

      if (Connection == ConnectionType.OUTBOUND)
        await ((AddressMessage)MessagesByCommand[AddressMessage.Command]).SendGetAddr(this);
    }

    internal string GetIP()
    {
      return SocketCommunication.GetIP();
    }

    async Task StartMessageReceiver()
    {
      try
      {
        while (true)
        {
          string commandMessage = await SocketCommunication.ReceiveCommandMessageNext();

          await SemaphorePeer.WaitAsync().ConfigureAwait(false);

          try
          {
            NetworkMessage message = MessagesByCommand.GetValueOrDefault(
              commandMessage,
              MessagesByCommand[UnknownMessage.Command]);

            message.LengthDataPayload = await SocketCommunication.ReceivePayloadNext(message.GetPayloadBuffer());

            message.IncrementDOSMonitor();

            await message.Run(this);
          }
          finally
          {
            SemaphorePeer.Release();
          }
        }
      }
      finally
      {
        StateCurrent = StateProtocol.Disposed;
        SocketCommunication.Dispose();
        HandshakeCompleted.TrySetCanceled();
      }
    }

    async Task StartHeartbeat()
    {
      while (true)
      {
        await Task.Delay(TIMESPAN_HEARTBEAT_MILLISECONDS).ConfigureAwait(false);

        if (IsDisposed())
          return;

        try
        {
          if (StateCurrent != StateProtocol.Handshake)
          {
            await ((PingMessage)MessagesByCommand[PingMessage.Command]).Ping(this);

            await RelayTXs();
          }

          if (!SemaphorePeer.Wait(0))
            continue;

          try
          {
            BlockMessage blockMessage = (BlockMessage)MessagesByCommand[BlockMessage.Command];

            if (blockMessage.BlockDownload.Header == null && StateCurrent != StateProtocol.AccountsDownload)
            {
              if (StateCurrent != StateProtocol.Idle)
                continue;

              await blockMessage.RequestBlockNext(this);
            }
            else if (DateTime.UtcNow - blockMessage.TimeRequest > TimeSpan.FromSeconds(TIMEOUT_REQUEST_SECONDS))
              SocketCommunication.Dispose();
          }
          finally
          {
            SemaphorePeer.Release();
          }
        }
        catch
        {
          SocketCommunication.Dispose();
        }
      }
    }
  }
}
