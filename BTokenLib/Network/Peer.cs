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
using System.Diagnostics;



namespace BTokenLib
{
  partial class Network
  {
    public partial class Peer
    {
      Network Network;
      public Token Token;

      public bool IsBusy;
      public bool FlagDispose;
      public bool FlagSyncScheduled;

      public enum StateProtocol
      {
        IDLE = 0,
        BlockDownload,
        HeaderDownload,
        GetData
      }
      public StateProtocol State;

      internal Header Header;
      internal Block Block;

      internal HeaderDownload HeaderDownload;
      internal Header HeaderUnsolicited;

      ulong FeeFilterValue;

      const string UserAgent = "/BTokenCore:0.0.0/";
      public enum ConnectionType { OUTBOUND, INBOUND };
      ConnectionType Connection;
      const UInt32 ProtocolVersion = 70013;
      public IPAddress IPAddress;
      TcpClient TcpClient;
      NetworkStream NetworkStream;
      CancellationTokenSource Cancellation = new();

      const int CommandSize = 12;
      const int LengthSize = 4;
      const int ChecksumSize = 4;

      public string Command;

      const int SIZE_MESSAGE_PAYLOAD_BUFFER = 0x100000;
      byte[] Payload = new byte[SIZE_MESSAGE_PAYLOAD_BUFFER];
      int PayloadLength;

      const int HeaderSize = CommandSize + LengthSize + ChecksumSize;
      byte[] MeassageHeader = new byte[HeaderSize];
      byte[] MagicBytes = new byte[4] { 0xF9, 0xBE, 0xB4, 0xD9 };

      SHA256 SHA256 = SHA256.Create();

      StreamWriter LogFile;

      DateTime TimePeerCreation = DateTime.Now;

      const int SECONDS_PEER_BANNED = 5;



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
          int secondsBannedRemaining = SECONDS_PEER_BANNED - (int)secondsSincePeerDisposal.TotalSeconds;

          if (secondsBannedRemaining > 0)
            throw new ProtocolException(
              $"Peer {this} is banned for {SECONDS_PEER_BANNED} seconds.\n" +
              $"{secondsBannedRemaining} seconds remaining.");

          File.Move(pathLogFileDisposed, pathLogFile);
        }

        LogFile = new StreamWriter(
          pathLogFile,
          append: true);
      }

      public async Task Connect()
      {
        $"Connect peer {this}".Log(LogFile);

        TcpClient = new();

        await TcpClient.ConnectAsync(IPAddress, Port)
          .ConfigureAwait(false);

        NetworkStream = TcpClient.GetStream();

        await SendMessage(new VersionMessage(
          protocolVersion: ProtocolVersion,
          networkServicesLocal: 0,
          unixTimeSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
          networkServicesRemote: 0,
          iPAddressRemote: IPAddress.Loopback,
          portRemote: Port,
          iPAddressLocal: IPAddress.Loopback,
          portLocal: Port,
          nonce: 0,
          userAgent: UserAgent,
          blockchainHeight: 0,
          relayOption: 0x01));

        await SendMessage(new VerAckMessage());

        StartMessageListener();
      }

      public async Task SendMessage(NetworkMessage message)
      {
        NetworkStream.Write(MagicBytes, 0, MagicBytes.Length);

        byte[] command = Encoding.ASCII.GetBytes(
          message.Command.PadRight(CommandSize, '\0'));

        NetworkStream.Write(command, 0, command.Length);

        byte[] payloadLength = BitConverter.GetBytes(message.LengthPayload);
        NetworkStream.Write(payloadLength, 0, payloadLength.Length);

        byte[] checksum = SHA256.ComputeHash(
          SHA256.ComputeHash(
            message.Payload,
            message.OffsetPayload,
            message.LengthPayload));

        NetworkStream.Write(checksum, 0, ChecksumSize);

        await NetworkStream.WriteAsync(
          message.Payload,
          message.OffsetPayload,
          message.LengthPayload)
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

      async Task ReadMessageBlock()
      {
        do
          await ReadMessage();
        while (Command != "block");

        FlagBlockReading = false;
      }

      async Task ReadMessage()
      {
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

        PayloadLength = BitConverter.ToInt32(
          MeassageHeader,
          CommandSize);

        if (PayloadLength > SIZE_MESSAGE_PAYLOAD_BUFFER)
          throw new ProtocolException(
            $"Message payload too big exceeding " +
            $"{SIZE_MESSAGE_PAYLOAD_BUFFER} bytes.");

        Command = Encoding.ASCII.GetString(
          MeassageHeader.Take(CommandSize)
          .ToArray()).TrimEnd('\0');

        if (Command == "block")
          await ReadBytes(Block.Buffer, PayloadLength);
        else
          await ReadBytes(Payload, PayloadLength);
      }


      internal Header HeaderDuplicateReceivedLast;
      internal List<Header> QueueHeadersUnsolicited = new();
      internal int CountOrphanReceived;

      public async Task StartMessageListener()
      {
        try
        {
          while (true)
          {
            if (FlagDispose)
              return;

            await ReadMessage();

            if (Command == "block")
            {
              Block.Parse();

              $"Peer {this} received block {Block}".Log(LogFile);

              if (IsStateIdle())
              {
                Console.Beep(1600, 100);

                if (QueueHeadersUnsolicited.Any())
                {
                  if (!Block.Header.Hash.IsEqual(QueueHeadersUnsolicited[0].Hash))
                    throw new ProtocolException(
                      $"Requested unsolicited block {QueueHeadersUnsolicited[0]}\n" +
                      $"instead received {Block.Header}");

                  QueueHeadersUnsolicited.RemoveAt(0);

                  Cancellation = new();
                }

                if (!Token.TryLock())
                  continue;

                try
                {
                  if (Block.Header.HashPrevious.IsEqual(
                    Token.Blockchain.HeaderTip.Hash))
                  {
                    Token.InsertBlock(Block, this);

                    $"{this}: Inserted unsolicited block {Block}."
                      .Log(LogFile);

                    Network.RelayBlock(Block, this);

                    if (QueueHeadersUnsolicited.Any())
                    {
                      HeaderUnsolicited = QueueHeadersUnsolicited[0];
                      ProcessHeaderUnsolicited();
                    }
                  }
                  else
                  {
                    QueueHeadersUnsolicited.Clear();
                    HandleHeaderUnsolicitedDuplicateOrOrphan(Block.Header);
                  }
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
                if (!Block.Header.Hash.IsEqual(Header.Hash))
                  throw new ProtocolException(
                    $"Unexpected block {Block} at height {Block.Header.Height}.\n" +
                    $"Excpected {Header}.");

                Cancellation = new();

                if (Network.InsertBlockFlagContinue(this))
                  await RequestBlock();
                else
                  Release();
              }
            }
            else if (Command == "ping")
            {
              await SendMessage(new PongMessage(
                BitConverter.ToUInt64(Payload, 0)));
            }
            else if (Command == "addr")
            {
              AddressMessage addressMessage = new(Payload);
            }
            else if (Command == "sendheaders")
            {
              await SendMessage(new SendHeadersMessage());
            }
            else if (Command == "feefilter")
            {
              FeeFilterMessage feeFilterMessage = new(Payload);
              FeeFilterValue = feeFilterMessage.FeeFilterValue;
            }
            else if (Command == "headers")
            {
              int byteIndex = 0;

              int countHeaders = VarInt.GetInt32(
                Payload,
                ref byteIndex);

              $"{this}: Receiving {countHeaders} headers."
                .Log(LogFile);

              if (IsStateGetHeaders())
              {
                bool flagRequestNoMoreHeaders = countHeaders == 0;

                try
                {
                  while (byteIndex < PayloadLength)
                  {
                    Header header = Token.ParseHeader(
                      Payload,
                      ref byteIndex);

                    byteIndex += 1;

                    HeaderDownload.InsertHeader(header, out flagRequestNoMoreHeaders);
                  }
                }
                catch (ProtocolException ex)
                {
                  ($"{ex.GetType().Name} when receiving headers:\n" +
                    $"{ex.Message}").Log(LogFile);

                  continue; // Does not disconnect on parser exception but on timeout instead.
                }

                if (flagRequestNoMoreHeaders)
                {
                  Cancellation = new();

                  Network.Sync();
                }
                else
                {
                  ($"Send getheaders to peer {this},\n" +
                    $"locator: {HeaderDownload.HeaderInsertedLast}").Log(LogFile);

                  await SendMessage(new GetHeadersMessage(
                    new List<Header> { HeaderDownload.HeaderInsertedLast },
                    ProtocolVersion));

                  Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);
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

                QueueHeadersUnsolicited.Add(HeaderUnsolicited);

                if (QueueHeadersUnsolicited.Count > 10)
                  throw new ProtocolException(
                    $"Too many ({QueueHeadersUnsolicited.Count}) headers unsolicited.");

                if (QueueHeadersUnsolicited.Count > 1)
                  break;

                ProcessHeaderUnsolicited();
              }
            }
            else if (Command == "getheaders")
            {
              byte[] hashHeaderAncestor = new byte[32];

              int startIndex = 4;

              int headersCount = VarInt.GetInt32(Payload, ref startIndex);

              $"Received getHeaders with {headersCount} headers.".Log(LogFile);

              int i = 0;
              List<Header> headers = new();

              while (i < headersCount)
              {
                Array.Copy(Payload, startIndex, hashHeaderAncestor, 0, 32);
                startIndex += 32;

                ($"Scan locator for common ancestor index {i}, " +
                  $"{hashHeaderAncestor.ToHexString()}").Log(LogFile);

                i += 1;

                if (Token.Blockchain.TryReadHeader(
                  hashHeaderAncestor,
                  out Header header))
                {
                  $"In getheaders locator common ancestor is {header}.".Log(LogFile);

                  while (header.HeaderNext != null && headers.Count < 2000)
                  {
                    headers.Add(header.HeaderNext);
                    header = header.HeaderNext;
                  }

                  if (headers.Any())
                    $"Send headers {headers.First()}...{headers.Last()}.".Log(LogFile);
                  else
                    $"Send empty headers".Log(LogFile);

                  await SendHeaders(headers);

                  break;
                }
                else if (i == headersCount)
                {
                  $"Found no common ancestor in getheaders locator... Schedule synchronization ".Log(LogFile);

                  await SendHeaders(headers);

                  FlagSyncScheduled = true;
                }
              }
            }
            else if (Command == "notfound")
            {
              "Received meassage notfound.".Log(LogFile);

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
              InvMessage invMessage = new(Payload);
              GetDataMessage getDataMessage = new(invMessage.Inventories);
            }
            else if (Command == "getdata")
            {
              GetDataMessage getDataMessage = new(Payload);

              foreach (Inventory inventory in getDataMessage.Inventories)
                if (inventory.Type == InventoryType.MSG_TX)
                {
                  if (Token.TryRequestTX(
                    inventory.Hash,
                    out byte[] tXRaw))
                  {
                    await SendMessage(new TXMessage(tXRaw));

                    string.Format(
                      "{0} received getData {1} and sent tXMessage {2}.",
                      this,
                      getDataMessage.Inventories[0],
                      inventory)
                      .Log(LogFile);
                  }
                  else
                  {
                    // Send notfound
                  }
                }
                else if (inventory.Type == InventoryType.MSG_BLOCK)
                {
                  Block block = Network.BlocksCached
                    .Find(b => b.Header.Hash.IsEqual(inventory.Hash));

                  if (block != null)
                    await SendMessage(new BlockMessage(block));
                }
                else
                  await SendMessage(new RejectMessage(inventory.Hash));
            }
          }
        }
        catch (Exception ex)
        {
          if (IsStateAwaitingHeader())
            Network.Token.ReleaseLock();
          else if (IsStateBlockDownload())
            Network.ReturnPeerBlockDownloadIncomplete(this);

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

            ($"{this}: Requested block for unsolicited " +
              $"header {HeaderUnsolicited}.")
              .Log(LogFile);
          }
          else
          {
            QueueHeadersUnsolicited.Remove(HeaderUnsolicited);
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
        if (Token.Blockchain.TryReadHeader(
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

            $"Stage synchronization because received orphan header {header}"
              .Log(LogFile);
          }
          else if (CountOrphanReceived > 10)
            throw new ProtocolException(
              "Too many orphan headers received.");
          else
            CountOrphanReceived += 1;
        }
      }

      public async Task AdvertizeToken(byte[] hash)
      {
        $"{this} advertize token {hash.ToHexString()}."
          .Log(LogFile);

        var inventoryTX = new Inventory(
          InventoryType.MSG_TX,
          hash);

        var invMessage = new InvMessage(
          new List<Inventory> { inventoryTX });

        await SendMessage(invMessage);

        Release();
      }
                            
      public async Task GetHeaders(HeaderDownload headerDownload)
      {
        HeaderDownload = headerDownload;
               
        ($"Send getheaders to peer {this},\n" +
          $"locator: {HeaderDownload}").Log(LogFile);

        SetStateHeaderDownload();

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetHeadersMessage(
          HeaderDownload.Locator,
          ProtocolVersion));
      }


      bool FlagBlockReading;

      public Block GetBlock(byte[] hash)
      {
        $"Peer {this} starts downloading block {hash.ToHexString()}."
          .Log(LogFile);

        SendMessage(new GetDataMessage(
          new List<Inventory>()
          {
            new Inventory(
              InventoryType.MSG_BLOCK,
              hash)
          }));

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        FlagBlockReading = true;

        ReadMessageBlock();

        while (FlagBlockReading)
        {
          Thread.Sleep(100);

          if (Cancellation.IsCancellationRequested)
            throw new TaskCanceledException(
              $"GetBlock {hash.ToHexString()} canceled.");
        }

        return Block;
      }

      public async Task RequestBlock()
      {
        $"Peer {this} starts downloading block {Header}.".Log(LogFile);

        lock (this)
        {
          if(FlagDispose)
            Network.ReturnPeerBlockDownloadIncomplete(this);

          State = StateProtocol.BlockDownload;
        }

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        try
        {
          await SendMessage(new GetDataMessage(
            new List<Inventory>()
            {
              new Inventory(
                InventoryType.MSG_BLOCK,
                Header.Hash)
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

      public bool TryGetBusy()
      {
        lock (this)
        {
          if ( FlagDispose || IsBusy)
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

      bool IsStateGetHeaders()
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

      public override string ToString()
      {
        return IPAddress.ToString();
      }

      public void SetFlagDisposed(string message)
      {
        $"Set flag dispose on peer {this}: {message}".Log(LogFile);

        lock (this)
          FlagDispose = true;
      }

      public void Dispose()
      {
        TcpClient.Dispose();

        LogFile.Dispose();

        File.Move(
          Path.Combine(Network.DirectoryLogPeers.FullName, ToString()),
          Path.Combine(Network.DirectoryLogPeersDisposed.FullName, ToString()));

        Debug.WriteLine($"Disposed {this}.");
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
