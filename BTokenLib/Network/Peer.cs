using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BTokenLib
{
  partial class Network
  {
    partial class Peer
    {
      Network Network;
      public Token Token;

      const int TIMEOUT_RESPONSE_MILLISECONDS = 5000;

      enum StateProtocol
      {
        NotConnected,
        Idle,
        HeaderSynchronization,
        BlockSynchronization,
        DBDownload,
        GetData,
        AdvertizingTX
      }

      StateProtocol State = StateProtocol.NotConnected;
      public DateTime TimeLastStateTransition;
      public DateTime TimeLastSynchronization;

      public Header HeaderSync;
      public Block Block;

      public byte[] HashDBDownload;
      public List<byte[]> HashesDB;

      public Header HeaderUnsolicited;
     
      List<TX> TXsAdvertized = new();

      ulong FeeFilterValue;

      const string UserAgent = "/BTokenCore:0.0.0/";
      public ConnectionType Connection;
      const UInt32 ProtocolVersion = 70015;
      public IPAddress IPAddress;
      TcpClient TcpClient;
      readonly object LOCK_FlagNetworkStreamIsLocked = new();
      bool FlagNetworkStreamIsLocked;
      NetworkStream NetworkStream;
      CancellationTokenSource Cancellation = new();
      const int TIMEOUT_VERACK_MILLISECONDS = 5000;

      const int CommandSize = 12;
      const int LengthSize = 4;
      const int ChecksumSize = 4;

      public string Command;

      const int SIZE_MESSAGE_PAYLOAD_BUFFER = 1 << 24; // 16 MB
      public byte[] Payload = new byte[SIZE_MESSAGE_PAYLOAD_BUFFER];
      public int LengthDataPayload;

      const int HeaderSize = CommandSize + LengthSize + ChecksumSize;
      byte[] MessageHeader = new byte[HeaderSize];
      byte[] MagicBytes = new byte[4] { 0xF9, 0xBE, 0xB4, 0xD9 };

      SHA256 SHA256 = SHA256.Create();

      StreamWriter LogFile;

      DateTime TimePeerCreation = DateTime.Now;



      public Peer(
        Network network,
        Token token,
        IPAddress ip,
        ConnectionType connection) : this(
          network, 
          token, 
          ip,
          new TcpClient(),
          connection)
      { }

      public Peer(
        Network network,
        Token token,
        IPAddress ip,
        TcpClient tcpClient,
        ConnectionType connection)
      {
        Network = network;
        Token = token;

        Block = Token.CreateBlock();

        TcpClient = tcpClient;
        IPAddress = ip;
        Connection = connection;

        CreateLogFile($"{ip}-{Connection}");
      }

      void CreateLogFile(string name)
      {
        string pathLogFileActive = Path.Combine(
          Network.DirectoryPeersActive.FullName,
          name);

        if (File.Exists(pathLogFileActive))
          throw new ProtocolException($"Peer {this} already active.");

        string pathLogFileDisposed = Path.Combine(
          Network.DirectoryPeersDisposed.FullName,
          name);

        if (File.Exists(pathLogFileDisposed))
        {
          TimeSpan secondsSincePeerDisposal = TimePeerCreation - File.GetLastWriteTime(pathLogFileDisposed);
          int secondsBannedRemaining = TIMESPAN_PEER_BANNED_SECONDS - (int)secondsSincePeerDisposal.TotalSeconds;

          if (secondsBannedRemaining > 0)
            throw new ProtocolException(
              $"Peer {this} is banned for {secondsBannedRemaining} seconds.");

          File.Move(pathLogFileDisposed, pathLogFileActive);
        }

        string pathLogFileArchive = Path.Combine(
          Network.DirectoryPeersArchive.FullName,
          name);

        if (File.Exists(pathLogFileArchive))
          File.Move(pathLogFileArchive, pathLogFileActive);

        LogFile = new StreamWriter(
          pathLogFileActive,
          append: true);
      }

      public async Task Connect()
      {
        $"Connect with peer {this}.".Log(LogFile);

        if (!TcpClient.Connected)
          await TcpClient.ConnectAsync(IPAddress, Network.Port)
            .ConfigureAwait(false);

        NetworkStream = TcpClient.GetStream();

        await SendMessage(new VersionMessage(
          protocolVersion: ProtocolVersion,
          networkServicesLocal: 0,
          unixTimeSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
          networkServicesRemote: 0,
          iPAddressRemote: IPAddress.Loopback,
          portRemote: Network.Port,
          iPAddressLocal: IPAddress.Loopback,
          portLocal: Network.Port,
          nonce: 0,
          userAgent: UserAgent,
          blockchainHeight: 0,
          relayOption: 0x01));

        await SendMessage(new VerAckMessage());

        $"Await verack.".Log(this, LogFile);
        ResetTimer(TIMEOUT_VERACK_MILLISECONDS);

        do
          await ListenForNextMessage();
        while (Command != "verack");

        $"Received verack.".Log(this, LogFile);
        ResetTimer();

        SetStateIdle();

        StartMessageListener();
      }

      async Task SendMessage(MessageNetwork message)
      {
        while (true)
        {
          lock (LOCK_FlagNetworkStreamIsLocked)
            if (!FlagNetworkStreamIsLocked)
            {
              FlagNetworkStreamIsLocked = true;
              break;
            }

          await Task.Delay(100).ConfigureAwait(true);
        }

        NetworkStream.Write(MagicBytes, 0, MagicBytes.Length);

        byte[] command = Encoding.ASCII.GetBytes(
          message.Command.PadRight(CommandSize, '\0'));
        NetworkStream.Write(command, 0, command.Length);

        byte[] payloadLength = BitConverter.GetBytes(message.LengthDataPayload);
        NetworkStream.Write(payloadLength, 0, payloadLength.Length);

        byte[] checksum = SHA256.ComputeHash(
          SHA256.ComputeHash(
            message.Payload,
            message.OffsetPayload,
            message.LengthDataPayload));

        NetworkStream.Write(checksum, 0, ChecksumSize);

        await NetworkStream.WriteAsync(
          message.Payload,
          message.OffsetPayload,
          message.LengthDataPayload)
          .ConfigureAwait(false);

        lock (LOCK_FlagNetworkStreamIsLocked)
          FlagNetworkStreamIsLocked = false;
      }

      public async Task SendGetHeaders(List<Header> locator)
      {
        ResetTimer(TIMEOUT_RESPONSE_MILLISECONDS);

        try
        {
          await SendMessage(new GetHeadersMessage(
            locator,
            ProtocolVersion));

          ($"Send getheaders to peer {this}\n" +
            $"locator: {locator.First()} ... {locator.Last()}")
            .Log(this, LogFile);
        }
        catch (Exception ex)
        {
          $"Exception {ex.GetType().Name} when sending getheaders message."
            .Log(LogFile);

          throw ex;
        }
      }

      void ResetTimer(int millisecondsTimer = int.MaxValue)
      {
        Cancellation.CancelAfter(millisecondsTimer);
      }

      public async Task<bool> TryAdvertizeTX(TX tX)
      {
        lock (this)
          if (State == StateProtocol.Idle)
            State = StateProtocol.AdvertizingTX;
          else
            return false;

        $"Advertize token {tX} to peer.".Log(this, LogFile);

        Inventory inventoryTX = new(
          InventoryType.MSG_TX,
          tX.Hash);

        InvMessage invMessage = new(
          new List<Inventory> { inventoryTX });

        await SendMessage(invMessage);

        TXsAdvertized.Clear();
        TXsAdvertized.Add(tX);

        SetStateIdle();

        return true;
      }

      public async Task<bool> TryAdvertizeTXs(List<TX> tXs)
      {
        lock (this)
          if (State == StateProtocol.Idle)
            State = StateProtocol.AdvertizingTX;
          else
            return false;

        TXsAdvertized.Clear();
        List<Inventory> inventories = new();

        tXs.ForEach(t =>
        {
          $"Advertize token {t} to peer {this}.".Log(LogFile);
          TXsAdvertized.Add(t);

          inventories.Add(new(
            InventoryType.MSG_TX,
            t.Hash));
        });

        await SendMessage(new InvMessage(inventories));

        SetStateIdle();

        return true;
      }

      public async Task RequestDB()
      {
        $"Peer starts downloading DB {HashDBDownload.ToHexString()}."
          .Log(this, LogFile);

        State = StateProtocol.DBDownload;

        ResetTimer(TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetDataMessage(
          new List<Inventory>()
          {
              new Inventory(
                InventoryType.MSG_DB,
                HashDBDownload)
          }));
      }

      public async Task RequestBlock(Header header = null)
      {
        if (header != null)
          HeaderSync = header;

        $"Peer starts downloading block {HeaderSync}."
          .Log(this, LogFile);

        State = StateProtocol.BlockSynchronization;

        ResetTimer(TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetDataMessage(
          new List<Inventory>()
          {
              new Inventory(
                InventoryType.MSG_BLOCK,
                HeaderSync.Hash)
          }));
      }

      public async Task SendHeaders(List<Header> headers)
      {
        string logText = $"Send {headers.Count} headers to peer {this}.";

        if (headers.Count > 0)
          logText += $" {headers.First()}";
        if (headers.Count > 1)
          logText += $" ... {headers.Last()}.";

        logText.Log(LogFile);

        await SendMessage(new HeadersMessage(headers));
      }

      public async Task AdvertizeBlock(Block block)
      {
        lock(this)
        {
          if(State != StateProtocol.Idle)
          {
            $"Peer {this} is not idle when attempting to send block {block} but in state {State.ToString()}.".Log(LogFile);
            return;
          }

          if (HeaderUnsolicited != null &&
            HeaderUnsolicited.Hash.IsEqual(block.Header.Hash))
          {
            $"Advertized block {block} was received by same peer {this}.".Log(LogFile);
            return;
          }

          State = StateProtocol.HeaderSynchronization;
        }

        $"Advertize block {block} to peer {this}.".Log(LogFile);

        await SendHeaders(new List<Header>() { block.Header });
        SetStateIdle();
      }

      public bool TrySync(Peer peerSyncCurrent = null)
      {
        lock (this)
        {
          if ((peerSyncCurrent == null || TimeLastSynchronization < peerSyncCurrent.TimeLastSynchronization)
            && State == StateProtocol.Idle)
          {
            State = StateProtocol.HeaderSynchronization;
            return true;
          }

          return false;
        }
      }

      public bool IsStateIdle()
      {
        lock (this)
          return State == StateProtocol.Idle;
      }

      public void SetStateIdle()
      {
        lock (this)
        {
          TimeLastStateTransition = DateTime.Now;
          State = StateProtocol.Idle;
        }
      }
      
      public void SetStateHeaderSynchronization()
      {
        lock (this)
        {
          TimeLastStateTransition = DateTime.Now;
          State = StateProtocol.HeaderSynchronization;
        }
      }

      public bool IsStateHeaderSynchronization()
      {
        lock (this)
          return State == StateProtocol.HeaderSynchronization;
      }

      bool IsStateAwaitingGetDataTX()
      {
        lock (this)
          return State == StateProtocol.GetData;
      }

      public bool IsStateBlockSynchronization()
      {
        lock (this)
          return State == StateProtocol.BlockSynchronization;
      }

      public bool IsStateDBDownload()
      {
        lock (this)
          return State == StateProtocol.DBDownload;
      }

      public void Dispose()
      {
        $"Dispose {Connection}".Log(this, LogFile);

        TcpClient.Dispose();

        LogFile.Dispose();

        string pathLogFile = ((FileStream)LogFile.BaseStream).Name;
        string nameLogFile = Path.GetFileName(pathLogFile);
        string pathLogFileDisposed = Path.Combine(
          Network.DirectoryPeersDisposed.FullName,
          nameLogFile);

        File.Move(pathLogFile, pathLogFileDisposed);
        File.SetCreationTime(pathLogFileDisposed, DateTime.Now);
      }

      public string GetStatus()
      {
        int lifeTime = (int)(DateTime.Now - TimePeerCreation).TotalMinutes;

        lock (this)
          return
            $"\nStatus peer {this}:\n" +
            $"lifeTime minutes: {lifeTime}\n" +
            $"State: {State}\n" +
            $"Connection: {Connection}\n";
      }

      public override string ToString()
      {
        return $"{Network} [{IPAddress}|{Connection}]";
      }
    }
  }
}
