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
using System.Runtime.CompilerServices;

namespace BTokenLib
{
  partial class Network
  {
    public partial class Peer
    {
      Network Network;
      public Token Token;

      enum StateProtocol
      {
        Idle = 0,
        HeaderSynchronization,
        BlockSynchronization,
        DBDownload,
        GetData,
        InboundRequest,
        Disposed
      }
      StateProtocol State;
      public DateTime TimeLastStateTransition;
      public DateTime TimeLastSynchronization;

      public Header HeaderSync;
      public Block Block;

      public byte[] HashDBDownload;
      public List<byte[]> HashesDB;

      public Header HeaderUnsolicited;
     
      TX TXAdvertized;

      ulong FeeFilterValue;

      const string UserAgent = "/BTokenCore:0.0.0/";
      public enum ConnectionType { OUTBOUND, INBOUND };
      public ConnectionType Connection;
      const UInt32 ProtocolVersion = 70015;
      public IPAddress IPAddress;
      TcpClient TcpClient;
      NetworkStream NetworkStream;
      CancellationTokenSource Cancellation = new();
      const int TIMEOUT_NEXT_SYNC_MILLISECONDS = 30000;

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
        TcpClient tcpClient)
        : this(
           network,
           token,
           ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address)
      {
        TcpClient = tcpClient;
        NetworkStream = tcpClient.GetStream();
        Connection = ConnectionType.INBOUND;
      }

      public Peer(
        Network network,
        Token token,
        IPAddress ip)
      {
        Network = network;
        Token = token;

        Block = Token.CreateBlock();

        IPAddress = ip;
        Connection = ConnectionType.OUTBOUND;

        CreateLogFile(ip.ToString());

        SetStateIdle();

        ResetTimer();
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
        $"Connect peer {Connection}.".Log(this, LogFile);

        TcpClient = new();

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

        StartMessageListener();

        if (Network.TryEnterStateSynchronization(this))
          SendGetHeaders(Network.HeaderDownload.Locator);
      }

      internal Header HeaderDuplicateReceivedLast;

      public async Task StartMessageListener()
      {
        do
          await ListenForNextMessage();
        while (Command != "verack");

        while (true)
          try
          {
            if (IsStateDiposed())
              return;

            await ListenForNextMessage();

            if (Command == "block")
            {
              if (!IsStateBlockSynchronization())
                throw new ProtocolException($"Received unrequested block message.");

              await ReadBytes(Block.Buffer, LengthDataPayload);

              Block.Parse();

              $"Peer received block {Block}".Log(this, LogFile);

              if (!Block.Header.Hash.IsEqual(HeaderSync.Hash))
                throw new ProtocolException(
                  $"Received unexpected block {Block} at height {Block.Header.Height} from peer {this}.\n" +
                  $"Requested was {HeaderSync}.");

              ResetTimer();

              if (Network.InsertBlock_FlagContinue(this))
                await RequestBlock();
              else
                SetStateIdle();
            }
            else if (Command == "dataDB")
            {
              await ReadBytes(Payload, LengthDataPayload);

              if (IsStateDBDownload())
              {
                byte[] hashDataDB = SHA256.ComputeHash(
                  Payload,
                  0,
                  LengthDataPayload);

                if (!hashDataDB.IsEqual(HashDBDownload))
                  throw new ProtocolException(
                    $"Unexpected dataDB with hash {hashDataDB.ToHexString()}.\n" +
                    $"Excpected hash {HashDBDownload.ToHexString()}.");

                ResetTimer();

                if (Network.InsertDB_FlagContinue(this))
                  await RequestDB();
                else
                  SetStateIdle();
              }
            }
            else if (Command == "ping")
            {
              $"Received ping message.".Log(LogFile);

              await ReadBytes(Payload, LengthDataPayload);

              await SendMessage(new PongMessage(Payload));
            }
            else if (Command == "addr")
            {
              await ReadBytes(Payload, LengthDataPayload);
              AddressMessage addressMessage = new(Payload);

              Network.AddNetworkAddressesAdvertized(
                addressMessage.NetworkAddresses);
            }
            else if (Command == "sendheaders")
            {
              await SendMessage(new SendHeadersMessage());
            }
            else if (Command == "feefilter")
            {
              await ReadBytes(Payload, LengthDataPayload);

              FeeFilterMessage feeFilterMessage = new(Payload);
              FeeFilterValue = feeFilterMessage.FeeFilterValue;
            }
            else if (Command == "headers")
            {
              await ReadBytes(Payload, LengthDataPayload);

              int byteIndex = 0;

              int countHeaders = VarInt.GetInt32(
                Payload,
                ref byteIndex);

              $"{this}: Receiving {countHeaders} headers.".Log(LogFile);

              if (!Network.TryEnterStateSynchronization(this))
                continue;

              if (countHeaders > 0)
              {
                Console.Beep(1200, 100);

                Header header = null;

                for (int i = 0; i < countHeaders; i += 1)
                {
                  header = Token.ParseHeader(
                    Payload,
                    ref byteIndex);

                  byteIndex += 1;

                  Network.InsertHeader(header);
                }

                await SendGetHeaders(new List<Header> { header });
              }
              else
              {
                //if (/*unsolicited zero header message*/)
                //  throw new ProtocolException($"Peer sent unsolicited empty header message.");

                ResetTimer();

                if (Token.FlagDownloadDBWhenSync(Network.HeaderDownload))
                {
                  await SendMessage(new GetHashesDBMessage());
                  ResetTimer(TIMEOUT_RESPONSE_MILLISECONDS);
                }
                else
                  Network.SyncBlocks();
              }
            }
            else if (Command == "getheaders")
            {
              $"Receive getheaders...".Log(this, LogFile);

              if (!Token.TryLock())
              {
                $"... but Token is locked.".Log(LogFile);
                continue;
              }

              if (!TrySetStateInboundRequest())
              {
                $"... but cannot change state {State} to state InboundRequest.".Log(LogFile);
                Token.ReleaseLock();
                continue;
              }

              await ReadBytes(Payload, LengthDataPayload);

              byte[] hashHeaderAncestor = new byte[32];

              int startIndex = 4;

              int headersCount = VarInt.GetInt32(Payload, ref startIndex);

              $"Received getHeaders with {headersCount} locator hashes."
                .Log(this, LogFile);

              int i = 0;
              List<Header> headers = new();

              while (true)
              {
                Array.Copy(Payload, startIndex, hashHeaderAncestor, 0, 32);
                startIndex += 32;

                if (Token.Blockchain.TryGetHeader(
                  hashHeaderAncestor,
                  out Header header))
                {
                  $"In getheaders locator common ancestor is {header}."
                    .Log(this, LogFile);

                  while (header.HeaderNext != null && headers.Count < 2000)
                  {
                    headers.Add(header.HeaderNext);
                    header = header.HeaderNext;
                  }

                  if (headers.Any())
                    $"Send headers {headers.First()}...{headers.Last()}.".Log(this, LogFile);
                  else
                    $"Send empty headers.".Log(this, LogFile);

                  await SendHeaders(headers);

                  break;
                }

                if (i++ == headersCount)
                  throw new ProtocolException($"Found no common ancestor in getheaders locator.");
              }

              Token.ReleaseLock();
            }
            else if (Command == "hashesDB")
            {
              await ReadBytes(Payload, LengthDataPayload);

              $"{this}: Receiving DB hashes.".Log(LogFile);

              HashesDB = Token.ParseHashesDB(
                Payload,
                LengthDataPayload,
                Network.HeaderDownload.HeaderTip);

              ResetTimer();

              Network.SyncDB(this);
            }
            else if (Command == "notfound")
            {
              await ReadBytes(Payload, LengthDataPayload);

              NotFoundMessage notFoundMessage = new(Payload);

              notFoundMessage.Inventories.ForEach(
                i => $"Did not find {i.Hash.ToHexString()}".Log(this, LogFile));

              if (IsStateBlockSynchronization())
                Network.ReturnPeerBlockDownloadIncomplete(this);
            }
            else if (Command == "inv")
            {
              await ReadBytes(Payload, LengthDataPayload);

              InvMessage invMessage = new(Payload);
              GetDataMessage getDataMessage = new(invMessage.Inventories);
            }
            else if (Command == "getdata")
            {
              if (!TrySetStateInboundRequest())
                continue;

              $"Received getData request from peer {this}.".Log(this, LogFile);

              await ReadBytes(Payload, LengthDataPayload);

              GetDataMessage getDataMessage = new(Payload);

              foreach (Inventory inventory in getDataMessage.Inventories)
                if (inventory.Type == InventoryType.MSG_TX &&
                  inventory.Hash.IsEqual(TXAdvertized.Hash))
                {
                  $"Received getData {inventory} from {this} and send tX {TXAdvertized}."
                    .Log(this, LogFile);

                  await SendMessage(new TXMessage(TXAdvertized.TXRaw.ToArray()));
                }
                else if (inventory.Type == InventoryType.MSG_BLOCK)
                {
                  Block block = Token.GetBlock(inventory.Hash);

                  if (block == null)
                    await SendMessage(new NotFoundMessage(
                      new List<Inventory>() { inventory }));
                  else
                    await SendMessage(new MessageBlock(block));
                }
                else if (inventory.Type == InventoryType.MSG_DB)
                {
                  if (Token.TryGetDB(inventory.Hash, out byte[] dataDB))
                    await SendMessage(new MessageDB(dataDB));
                }
                else
                  await SendMessage(new RejectMessage(inventory.Hash));
            }
            else if (Command == "reject")
            {
              await ReadBytes(Payload, LengthDataPayload);

              RejectMessage rejectMessage = new(Payload);

              $"Peer {this} gets reject message: {rejectMessage.GetReasonReject()}"
                .Log(this, LogFile);
            }
          }
          catch (Exception ex)
          {
            Network.HandleExceptionPeerListener(this);

            SetStateDisposed($"{ex.GetType().Name} in listener: \n{ex.Message}");
            break;
          }
      }

      async Task ListenForNextMessage()
      {
        byte[] magicByte = new byte[1];

        for (int i = 0; i < MagicBytes.Length; i++)
        {
          await ReadBytes(magicByte, 1);

          if (MagicBytes[i] != magicByte[0])
            i = magicByte[0] == MagicBytes[0] ? 0 : -1;
        }

        await ReadBytes(
          MessageHeader,
          MessageHeader.Length);

        LengthDataPayload = BitConverter.ToInt32(
          MessageHeader,
          CommandSize);

        if (LengthDataPayload > SIZE_MESSAGE_PAYLOAD_BUFFER)
          throw new ProtocolException(
            $"Message payload too big exceeding " +
            $"{SIZE_MESSAGE_PAYLOAD_BUFFER} bytes.");

        Command = Encoding.ASCII.GetString(
          MessageHeader.Take(CommandSize)
          .ToArray()).TrimEnd('\0');
      }

      async Task SendMessage(MessageNetwork message)
      {
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
      }

      async Task ReadBytes(
        byte[] buffer,
        int bytesToRead)
      {
        int offset = 0;

        while (bytesToRead > 0)
        {
          int chunkSize = await NetworkStream.ReadAsync(
            buffer,
            offset,
            bytesToRead,
            Cancellation.Token)
            .ConfigureAwait(false);

          if (chunkSize == 0)
            throw new IOException(
              "Stream returns 0 bytes signifying end of stream.");

          offset += chunkSize;
          bytesToRead -= chunkSize;
        }
      }

      public async Task SendGetHeaders(
        List<Header> locator)
      {
        ($"Send getheaders to peer {this}\n" +
          $"locator: {locator.First()} ... {locator.Last()}").Log(this, LogFile);

        ResetTimer(TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetHeadersMessage(
          locator,
          ProtocolVersion));
      }

      void ResetTimer(int millisecondsTimer = TIMEOUT_NEXT_SYNC_MILLISECONDS)
      {
        Cancellation.CancelAfter(millisecondsTimer);
      }

      public async Task AdvertizeTX(TX tX)
      {
        $"Advertize token {tX} to peer."
          .Log(this, LogFile);

        var inventoryTX = new Inventory(
          InventoryType.MSG_TX,
          tX.Hash);

        var invMessage = new InvMessage(
          new List<Inventory> { inventoryTX });

        await SendMessage(invMessage);

        TXAdvertized = tX;

        SetStateIdle();
      }

      public async Task RequestDB()
      {
        $"Peer starts downloading DB {HashDBDownload.ToHexString()}."
          .Log(this, LogFile);

        State = StateProtocol.DBDownload;

        ResetTimer(TIMEOUT_RESPONSE_MILLISECONDS);

        try
        {
          await SendMessage(new GetDataMessage(
            new List<Inventory>()
            {
              new Inventory(
                InventoryType.MSG_DB,
                HashDBDownload)
            }));
        }
        catch (Exception ex)
        {
          SetStateDisposed(
            $"{ex.GetType().Name} when sending getBlock message: {ex.Message}");

          Network.ReturnPeerDBDownloadIncomplete(HashDBDownload);
          return;
        }
      }

      public async Task RequestBlock()
      {
        $"Peer starts downloading block {HeaderSync}.".Log(this, LogFile);

        State = StateProtocol.BlockSynchronization;

        ResetTimer(TIMEOUT_RESPONSE_MILLISECONDS);

        try
        {
          await SendMessage(new GetDataMessage(
            new List<Inventory>()
            {
              new Inventory(
                InventoryType.MSG_BLOCK,
                HeaderSync.Hash)
            }));
        }
        catch(Exception ex)
        {
          SetStateDisposed(
            $"{ex.GetType().Name} when sending getBlock message: {ex.Message}");

          Network.ReturnPeerBlockDownloadIncomplete(this);
          return;
        }
      }

      public async Task SendHeaders(List<Header> headers)
      {
        await SendMessage(new HeadersMessage(headers));
      }

      public async Task AdvertizeBlock(Block block)
      {
        $"Relay block {block} to peer.".Log(this, LogFile);

        await SendHeaders(new List<Header>() { block.Header });
        SetStateIdle();
      }

      public bool TrySync()
      {
        lock (this)
        {
          if (IsStateIdleNOTLocked())
          {
            State = StateProtocol.HeaderSynchronization;
            return true;
          }

          return false;
        }
      }

      bool IsStateIdleNOTLocked()
      {
        if (State == StateProtocol.Idle)
          return true;
        else if (State == StateProtocol.InboundRequest)
          if ((DateTime.Now - TimeLastStateTransition).TotalMilliseconds > TIMEOUT_RESPONSE_MILLISECONDS)
          {
            TimeLastStateTransition = DateTime.Now;
            State = StateProtocol.Idle;
            return true;
          }

        return false;
      }

      public bool IsStateIdle()
      {
        lock (this) 
          return IsStateIdleNOTLocked();
      }

      public void SetStateIdle()
      {
        lock (this)
        {
          TimeLastStateTransition = DateTime.Now;
          State = StateProtocol.Idle;
        }
      }

      bool TrySetStateInboundRequest()
      {
        lock (this)
        {
          if (State != StateProtocol.InboundRequest &&
            State != StateProtocol.Idle)
            return false;

          State = StateProtocol.InboundRequest;
          TimeLastStateTransition = DateTime.Now;
          return true;
        }
      }

      public void SetStateHeaderSynchronization()
      {
        lock (this)
          State = StateProtocol.HeaderSynchronization;
      }

      public bool IsStateDiposed()
      {
        lock (this)
          return State == StateProtocol.Disposed;
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

      public void SetStateDisposed(string message)
      {
        $"Set state dispose on peer {Connection}: {message}".Log(this, LogFile);

        lock (this)
          State = StateProtocol.Disposed;
      }

      public void Dispose()
      {
        $"Dispose {Connection}".Log(this, LogFile);

        TcpClient.Dispose();

        LogFile.Dispose();

        string pathLogFile = ((FileStream)LogFile.BaseStream).Name;

        File.Move(
          pathLogFile,
          Path.Combine(Network.DirectoryPeersDisposed.FullName, IPAddress.ToString()));
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
        return Network + "{" + IPAddress.ToString() + "}";
      }
    }
  }
}
