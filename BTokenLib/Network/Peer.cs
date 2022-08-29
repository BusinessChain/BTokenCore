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
    public partial class Peer
    {
      Network Network;
      public Token Token;

      public bool IsBusy = true;
      public bool FlagDispose;
      public bool FlagSyncScheduled;

      public enum StateProtocol
      {
        IDLE = 0,
        BlockDownload,
        DBDownload,
        HeaderDownload,
        GetData
      }
      public StateProtocol State;

      internal Header HeaderSync;
      internal Block Block;

      internal byte[] HashDBSync;

      internal HeaderDownload HeaderDownload;
      internal Header HeaderUnsolicited;

      ulong FeeFilterValue;

      const string UserAgent = "/BTokenCore:0.0.0/";
      public enum ConnectionType { OUTBOUND, INBOUND };
      public ConnectionType Connection;
      const UInt32 ProtocolVersion = 70015;
      public IPAddress IPAddress;
      TcpClient TcpClient;
      NetworkStream NetworkStream;
      CancellationTokenSource Cancellation = new();

      const int CommandSize = 12;
      const int LengthSize = 4;
      const int ChecksumSize = 4;

      public string Command;

      // Payload buffer does not accomodate Block data, which is read into the block buffer directly
      const int SIZE_MESSAGE_PAYLOAD_BUFFER = 1000000; 
      byte[] Payload = new byte[SIZE_MESSAGE_PAYLOAD_BUFFER];
      int LengthDataPayload;

      const int HeaderSize = CommandSize + LengthSize + ChecksumSize;
      byte[] MeassageHeader = new byte[HeaderSize];
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
        FlagSyncScheduled = false;
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
        FlagSyncScheduled = true;

        CreateLogFile(ip.ToString());

        State = StateProtocol.IDLE;
      }


      void CreateLogFile(string name)
      {
        string pathLogFile = Path.Combine(Network.DirectoryLogPeers.FullName, name);
        string pathLogFileDisposed = Path.Combine(
          Network.DirectoryLogPeersDisposed.FullName, 
          name);

        if (File.Exists(pathLogFileDisposed))
        {
          TimeSpan secondsSincePeerDisposal = TimePeerCreation - File.GetLastWriteTime(pathLogFileDisposed);
          int secondsBannedRemaining = TIMESPAN_PEER_BANNED_SECONDS - (int)secondsSincePeerDisposal.TotalSeconds;

          if (secondsBannedRemaining > 0)
            throw new ProtocolException(
              $"Peer {this} is banned for {TIMESPAN_PEER_BANNED_SECONDS} seconds.\n" +
              $"{secondsBannedRemaining} seconds remaining.");

          File.Move(pathLogFileDisposed, pathLogFile);
        }

        LogFile = new StreamWriter(
          pathLogFile,
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
      }

      public async Task SendMessage(MessageNetwork message)
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


      internal Header HeaderDuplicateReceivedLast;
      internal int CountOrphanReceived;

      public async Task StartMessageListener()
      {
        try
        {
          while (true)
          {
            if (FlagDispose)
              return;

            byte[] magicByte = new byte[1];

            for (int i = 0; i < MagicBytes.Length; i++)
            {
              await ReadBytes(magicByte, 1);

              if (MagicBytes[i] != magicByte[0])
                i = magicByte[0] == MagicBytes[0] ? 0 : -1;
            }

            await ReadBytes(
              MeassageHeader,
              MeassageHeader.Length);

            LengthDataPayload = BitConverter.ToInt32(
              MeassageHeader,
              CommandSize);

            if (LengthDataPayload > SIZE_MESSAGE_PAYLOAD_BUFFER)
              throw new ProtocolException(
                $"Message payload too big exceeding " +
                $"{SIZE_MESSAGE_PAYLOAD_BUFFER} bytes.");

            Command = Encoding.ASCII.GetString(
              MeassageHeader.Take(CommandSize)
              .ToArray()).TrimEnd('\0');

            if (Command == "block")
            {
              await ReadBytes(Block.Buffer, LengthDataPayload);

              Block.Parse();

              $"Peer received block {Block}".Log(this, LogFile);

              if (IsStateIdle())
              {
                Console.Beep(1600, 100);

                Cancellation = new();

                if (!Token.TryLock())
                  continue;

                try
                {
                  if (Block.Header.HashPrevious.IsEqual(
                    Token.Blockchain.HeaderTip.Hash))
                  {
                    Token.InsertBlock(Block);

                    $"Inserted unsolicited block {Block}."
                      .Log(this, LogFile);

                    Network.RelayBlockToNetwork(Block, this);
                  }
                  else
                    HandleHeaderUnsolicitedDuplicateOrOrphan(Block.Header);
                }
                catch (ProtocolException ex)
                {
                  SetFlagDisposed(ex.Message);
                }
                finally
                {
                  Token.ReleaseLock();
                }
              }
              else if (IsStateBlockDownload())
              {
                if (!Block.Header.Hash.IsEqual(HeaderSync.Hash))
                  throw new ProtocolException(
                    $"Unexpected block {Block} at height {Block.Header.Height}.\n" +
                    $"Excpected {HeaderSync}.");

                Cancellation = new();

                if (Network.InsertBlock_FlagContinue(this))
                  await RequestBlock();
                else
                  Release();
              }
            }
            else if(Command == "dataDB")
            {
              await ReadBytes(Payload, LengthDataPayload);

              if (IsStateDBDownload())
              {
                byte[] hashDataDB = SHA256(Payload, LengthDataPayload); 

                if (!hashDataDB.IsEqual(HashDBSync))
                  throw new ProtocolException(
                    $"Unexpected dataDB with hash {hashDataDB.ToHexString()}.\n" +
                    $"Excpected hash {HashDBSync.ToHexString()}.");

                Cancellation = new();

                if (Network.InsertDB_FlagContinue(this))
                  await RequestDB();
                else
                  Release();
              }
            }
            else if (Command == "ping")
            {
              await ReadBytes(Payload, LengthDataPayload);

              await SendMessage(new PongMessage(
                BitConverter.ToUInt64(Payload, 0)));
            }
            else if (Command == "addr")
            {
              await ReadBytes(Payload, LengthDataPayload);
              AddressMessage addressMessage = new(Payload);
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

              $"{this}: Receiving {countHeaders} headers."
                .Log(LogFile);

              if (IsStateHeaderDownload())
              {
                if (countHeaders > 0)
                {
                  try
                  {
                    for (int i = 0; i < countHeaders; i += 1)
                    {
                      Header header = Token.ParseHeader(
                        Payload,
                        ref byteIndex);

                      byteIndex += 1;

                      HeaderDownload.InsertHeader(header);
                    }
                  }
                  catch (ProtocolException ex)
                  {
                    ($"{ex.GetType().Name} when receiving headers:\n" +
                      $"{ex.Message}").Log(LogFile);

                    continue; // Does not disconnect on parser exception but on timeout instead.
                  }

                  ($"Send getheaders to peer {this},\n" +
                    $"locator: {HeaderDownload.HeaderInsertedLast}").Log(LogFile);

                  await SendMessage(new GetHeadersMessage(
                    new List<Header> { HeaderDownload.HeaderInsertedLast },
                    ProtocolVersion));

                  Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);
                }
                else
                {
                  Cancellation = new();

                  Network.Sync(this);
                }
              }
              else
              {
                HeaderUnsolicited = Token.ParseHeader(
                  Payload,
                  ref byteIndex);

                $"Parsed unsolicited header {HeaderUnsolicited}.".Log(LogFile);

                Console.Beep(800, 100);

                Network.ThrottleDownloadBlockUnsolicited();

                ProcessHeaderUnsolicited();
              }
            }
            else if (Command == "getheaders")
            {
              await ReadBytes(Payload, LengthDataPayload);

              byte[] hashHeaderAncestor = new byte[32];

              int startIndex = 4;

              int headersCount = VarInt.GetInt32(Payload, ref startIndex);

              $"Received getHeaders with {headersCount} locator hashes."
                .Log(this, LogFile);

              int i = 0;
              List<Header> headers = new();

              while (i < headersCount)
              {
                Array.Copy(Payload, startIndex, hashHeaderAncestor, 0, 32);
                startIndex += 32;

                ($"Scan locator for common ancestor index {i}, " +
                  $"{hashHeaderAncestor.ToHexString()}")
                  .Log(this, LogFile);

                i += 1;

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
                    $"Send empty headers".Log(this, LogFile);

                  await SendHeaders(headers);

                  break;
                }
                else if (i == headersCount)
                {
                  ($"Found no common ancestor in getheaders locator... " +
                    $"Schedule synchronization ").Log(LogFile);

                  await SendHeaders(headers);

                  FlagSyncScheduled = true;
                }
              }
            }
            else if (Command == "hashesDB")
            {
              await ReadBytes(Payload, LengthDataPayload);

              $"{this}: Receiving DB hashes.".Log(LogFile);

              List<byte[]> hashesDB = Token.ParseHashesDB(Payload);

              Cancellation = new();

              Network.SyncDB(hashesDB);
            }
            else if (Command == "notfound")
            {
              await ReadBytes(Payload, LengthDataPayload);

              "Received meassage notfound.".Log(this, LogFile);

              if (IsStateBlockDownload())
              {
                Network.ReturnPeerBlockDownloadIncomplete(this);

                lock (this)
                  State = StateProtocol.IDLE;

                if (this == Network.PeerSync)
                  throw new ProtocolException(
                    $"Peer has sent headers but does not deliver blocks.");
              }
            }
            else if (Command == "inv")
            {
              await ReadBytes(Payload, LengthDataPayload);

              InvMessage invMessage = new(Payload);
              GetDataMessage getDataMessage = new(invMessage.Inventories);
            }
            else if (Command == "getdata")
            {
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
                  // Bei Bitcoin werden die Blöcke ja nicht abgespeichert deshalb existiert allenfalls nur ein Cache

                  Block block = Network.BlocksCached
                    .Find(b => b.Header.Hash.IsEqual(inventory.Hash));

                  if (block != null)
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
        }
        catch (Exception ex)
        {
          if (IsStateAwaitingHeader())
            Token.ReleaseLock();
          else if (IsStateBlockDownload())
            Network.ReturnPeerBlockDownloadIncomplete(this);
          else if (IsStateDBDownload())
            Network.ReturnPeerDBDownloadIncomplete(HashDBSync);

          SetFlagDisposed(
            $"{ex.GetType().Name} in listener: \n{ex.Message}");
        }
      }

      async Task ProcessHeaderUnsolicited()
      {
        try
        {
          if (HeaderUnsolicited.HashPrevious.IsEqual(
            Token.Blockchain.HeaderTip.Hash))
          {
            await SendMessage(new GetDataMessage(
              new List<Inventory>()
              {
                new Inventory(
                  InventoryType.MSG_BLOCK,
                  HeaderUnsolicited.Hash)
              }));

            Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

            ($"Requested block for unsolicited " +
              $"header {HeaderUnsolicited}.")
              .Log(this, LogFile);
          }
          else
          {
            HandleHeaderUnsolicitedDuplicateOrOrphan(HeaderUnsolicited);
          }
        }
        catch (ProtocolException ex)
        {
          SetFlagDisposed(ex.Message);
        }
      }

      void HandleHeaderUnsolicitedDuplicateOrOrphan(Header header)
      {
        if (Token.Blockchain.TryGetHeader(
          header.Hash,
          out Header headerReceivedNow))
        {
          if (HeaderDuplicateReceivedLast != null &&
            HeaderDuplicateReceivedLast.Height >= headerReceivedNow.Height)
            throw new ProtocolException($"Sent duplicate header {header}.");

          HeaderDuplicateReceivedLast = headerReceivedNow;
        }
        else
        {
          if (!FlagSyncScheduled)
          {
            CountOrphanReceived = 0;
            FlagSyncScheduled = true;

            $"Schedule synchronization because received orphan header {header}"
              .Log(this, LogFile);
          }
          else if (CountOrphanReceived > 10)
            throw new ProtocolException(
              "Too many orphan headers received.");
          else
            CountOrphanReceived += 1;
        }
      }


      TX TXAdvertized;

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

        Release();
      }
                      
      
      public async Task GetHeaders()
      {
        HeaderDownload = Token.CreateHeaderDownload();

        ($"Send getheaders to peer,\n" +
          $"locator: {HeaderDownload}").Log(this, LogFile);

        SetStateHeaderDownload();

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetHeadersMessage(
          HeaderDownload.Locator,
          ProtocolVersion));
      }

      public async Task RequestDB()
      {
        $"Peer starts downloading DB {HashDBSync.ToHexString()}."
          .Log(this, LogFile);

        lock (this)
        {
          if (FlagDispose)
            Network.ReturnPeerDBDownloadIncomplete(HashDBSync);

          State = StateProtocol.DBDownload;
        }

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        try
        {
          await SendMessage(new GetDataMessage(
            new List<Inventory>()
            {
              new Inventory(
                InventoryType.MSG_DB,
                HashDBSync)
            }));
        }
        catch (Exception ex)
        {
          SetFlagDisposed(
            $"{ex.GetType().Name} when sending getBlock message: {ex.Message}");

          Network.ReturnPeerDBDownloadIncomplete(HashDBSync);
          return;
        }
      }

      public async Task RequestBlock()
      {
        $"Peer starts downloading block {HeaderSync}.".Log(this, LogFile);

        lock (this)
        {
          State = StateProtocol.BlockDownload;

          if (FlagDispose)
            Network.ReturnPeerBlockDownloadIncomplete(this);
        }

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

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
          SetFlagDisposed(
            $"{ex.GetType().Name} when sending getBlock message: {ex.Message}");

          Network.ReturnPeerBlockDownloadIncomplete(this);
          return;
        }
      }

      public async Task SendHeaders(List<Header> headers)
      {
        await SendMessage(new HeadersMessage(headers));
      }

      public async Task RelayBlock(Block block)
      {
        $"Relay block {block} to peer.".Log(this, LogFile);

        await SendHeaders(new List<Header>() { block.Header });
        Release();
      }

      public bool TryGetBusy()
      {
        lock (this)
        {
          if (FlagDispose || IsBusy)
            return false;

          IsBusy = true;
          return true;
        }
      }

      public bool TrySync()
      {
        lock(this)
        {
          if (
            !FlagSyncScheduled ||
            FlagDispose ||
            IsBusy)
          {
            return false;
          }

          FlagSyncScheduled = false;
          IsBusy = true;
          return true;
        }
      }

      public void Release()
      {
        lock (this)
        {
          IsBusy = false;
          State = StateProtocol.IDLE;
        }
      }

      public bool IsStateIdle()
      {
        lock (this)
          return State == StateProtocol.IDLE;
      }

      public void SetStateHeaderDownload()
      {
        lock (this)
          State = StateProtocol.HeaderDownload;
      }

      bool IsStateHeaderDownload()
      {
        lock (this)
          return State == StateProtocol.HeaderDownload;
      }

      bool IsStateAwaitingGetDataTX()
      {
        lock (this)
          return State == StateProtocol.GetData;
      }

      public bool IsStateAwaitingHeader()
      {
        lock (this)
          return State == StateProtocol.HeaderDownload;
      }

      public bool IsStateBlockDownload()
      {
        lock (this)
          return State == StateProtocol.BlockDownload;
      }

      public bool IsStateDBDownload()
      {
        lock (this)
          return State == StateProtocol.DBDownload;
      }

      public override string ToString()
      {
        return Network + "{" + IPAddress.ToString() + "}";
      }

      public void SetFlagDisposed(string message)
      {
        $"Set flag dispose on peer {Connection}: {message}".Log(this, LogFile);

        lock (this)
          FlagDispose = true;
      }

      public void Dispose()
      {
        Dispose(flagBanPeer: true);
      }

      public void Dispose(bool flagBanPeer)
      {
        $"Dispose {Connection}".Log(this, LogFile);

        TcpClient.Dispose();

        LogFile.Dispose();

        if (flagBanPeer)
          File.Move(
            Path.Combine(Network.DirectoryLogPeers.FullName, IPAddress.ToString()),
            Path.Combine(Network.DirectoryLogPeersDisposed.FullName, IPAddress.ToString()));
      }

      public string GetStatus()
      {
        int lifeTime = (DateTime.Now - TimePeerCreation).Seconds;
        
        lock (this)
          return
            $"\n Status peer {this}:\n" +
            $"lifeTime: {lifeTime}\n" +
            $"IsBusy: {IsBusy}\n" +
            $"State: {State}\n" +
            $"FlagDispose: {FlagDispose}\n" +
            $"Connection: {Connection}\n" +
            $"FlagSynchronizationScheduled: {FlagSyncScheduled}\n";
      }
    }
  }
}
