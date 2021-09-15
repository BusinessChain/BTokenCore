using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
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
      public enum ServiceFlags : UInt64
      {
        // Nothing
        NODE_NONE = 0,
        // NODE_NETWORK means that the node is capable of serving the complete block chain. It is currently
        // set by all Bitcoin Core non pruned nodes, and is unset by SPV clients or other light clients.
        NODE_NETWORK = (1 << 0),
        // NODE_GETUTXO means the node is capable of responding to the getutxo protocol request.
        // Bitcoin Core does not support this but a patch set called Bitcoin XT does.
        // See BIP 64 for details on how this is implemented.
        NODE_GETUTXO = (1 << 1),
        // NODE_BLOOM means the node is capable and willing to handle bloom-filtered connections.
        // Bitcoin Core nodes used to support this by default, without advertising this bit,
        // but no longer do as of protocol version 70011 (= NO_BLOOM_VERSION)
        NODE_BLOOM = (1 << 2),
        // NODE_WITNESS indicates that a node can be asked for blocks and transactions including
        // witness data.
        NODE_WITNESS = (1 << 3),
        // NODE_XTHIN means the node supports Xtreme Thinblocks
        // If this is turned off then the node will not service nor make xthin requests
        NODE_XTHIN = (1 << 4),
        // NODE_NETWORK_LIMITED means the same as NODE_NETWORK with the limitation of only
        // serving the last 288 (2 day) blocks
        // See BIP159 for details on how this is implemented.
        NODE_NETWORK_LIMITED = (1 << 10),


        // Bits 24-31 are reserved for temporary experiments. Just pick a bit that
        // isn't getting used, or one not being used much, and notify the
        // bitcoin-development mailing list. Remember that service bits are just
        // unauthenticated advertisements, so your code must be robust against
        // collisions and other cases where nodes may be advertising a service they
        // do not actually support. Other service bits should be allocated via the
        // BIP process.
      }

      Network Network;
      Blockchain Blockchain;
      Token Token;

      public bool IsBusy;
      public bool FlagDispose;
      public bool IsSynchronized;

      const int COUNT_BLOCKS_DOWNLOADBATCH_INIT = 1;
      public int CountBlocksLoad = COUNT_BLOCKS_DOWNLOADBATCH_INIT;

      internal HeaderDownload HeaderDownload = new();
      internal BlockDownload BlockDownload;


      ulong FeeFilterValue;

      const ServiceFlags NetworkServicesRemoteRequired =
        ServiceFlags.NODE_NONE;

      const ServiceFlags NetworkServicesLocal =
        ServiceFlags.NODE_NETWORK;

      const string UserAgent = "/BTokenCore:0.0.0/";
      const Byte RelayOption = 0x01;
      readonly static ulong Nonce = CreateNonce();
      static ulong CreateNonce()
      {
        Random rnd = new Random();

        ulong number = (ulong)rnd.Next();
        number = number << 32;
        return number |= (uint)rnd.Next();
      }
      public enum ConnectionType { OUTBOUND, INBOUND };
      ConnectionType Connection;
      const UInt32 ProtocolVersion = 70015;
      public IPAddress IPAddress;
      TcpClient TcpClient;
      NetworkStream NetworkStream;
      CancellationTokenSource Cancellation = new();

      public StateProtocol State;

      public enum StateProtocol
      {
        IDLE = 0,
        AwaitingBlockDownload,
        AwaitingHeader,
        AwaitingGetData
      }

      const int CommandSize = 12;
      const int LengthSize = 4;
      const int ChecksumSize = 4;

      public string Command;

      const int SIZE_MESSAGE_PAYLOAD_BUFFER = 0x400000;
      byte[] Payload = new byte[SIZE_MESSAGE_PAYLOAD_BUFFER];
      int PayloadLength;

      const int HeaderSize = CommandSize + LengthSize + ChecksumSize;
      byte[] MeassageHeader = new byte[HeaderSize];
      byte[] MagicBytes = new byte[4] { 0xF9, 0xBE, 0xB4, 0xD9 };

      SHA256 SHA256 = SHA256.Create();

      StreamWriter LogFile;
      string PathLogFile;



      public Peer(
        Network network,
        Blockchain blockchain,
        Token token,
        TcpClient tcpClient)
        : this(
           network,
           blockchain,
           token,
           ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString())
      {
        TcpClient = tcpClient;
        NetworkStream = tcpClient.GetStream();

        Connection = ConnectionType.INBOUND;
      }

      public Peer(
        Network network,
        Blockchain blockchain,
        Token token,
        IPAddress iPAddress) 
        :this(
           network,
           blockchain,
           token,
           iPAddress.ToString())
      {
        Connection = ConnectionType.OUTBOUND;
        IPAddress = iPAddress;
      }

      Peer(
        Network network,
        Blockchain blockchain,
        Token token,
        string name)
      {
        Network = network;
        Blockchain = blockchain;
        Token = token;

        CreateLogFile(name);

        State = StateProtocol.IDLE;
      }


      void CreateLogFile(string name)
      {
        PathLogFile = Path.Combine(
          DirectoryLogPeers.Name,
          name);

        while(true)
        {
          try
          {
            LogFile = new StreamWriter(PathLogFile, true);

            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              $"Cannot create logfile file peer {GetID()}: {ex.Message}");

            Thread.Sleep(10000);
          }
        }
      }

      public async Task Connect(int port)
      {
        $"Connect peer {GetID()}"
          .Log(LogFile);

        TcpClient = new TcpClient();

        await TcpClient.ConnectAsync(
          IPAddress,
          port).ConfigureAwait(false);

        NetworkStream = TcpClient.GetStream();

        await HandshakeAsync(port);

        $"Network protocol handshake {GetID()}"
          .Log(LogFile);

        StartMessageListener();
      }

      async Task HandshakeAsync(int port)
      {
        VersionMessage versionMessage = new()
        {
          ProtocolVersion = ProtocolVersion,
          NetworkServicesLocal = (long)NetworkServicesLocal,
          UnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
          NetworkServicesRemote = (long)NetworkServicesRemoteRequired,
          IPAddressRemote = IPAddress.Loopback.MapToIPv6(),
          PortRemote = (ushort)port,
          IPAddressLocal = IPAddress.Loopback.MapToIPv6(),
          PortLocal = (ushort)port,
          Nonce = Nonce,
          UserAgent = UserAgent,
          BlockchainHeight = Blockchain.HeaderTip.Height,
          RelayOption = RelayOption
        };

        versionMessage.SerializePayload();

        await SendMessage(versionMessage);

        CancellationToken cancellationToken =
          new CancellationTokenSource(TimeSpan.FromSeconds(5))
          .Token;

        bool verAckReceived = false;
        bool versionReceived = false;

        while (!verAckReceived || !versionReceived)
        {
          await ReadMessage(cancellationToken);

          switch (Command)
          {
            case "verack":
              verAckReceived = true;
              break;

            case "version":
              var versionMessageRemote = new VersionMessage(Payload);

              versionReceived = true;
              string rejectionReason = "";

              if (versionMessageRemote.ProtocolVersion < ProtocolVersion)
              {
                rejectionReason = string.Format(
                  "Outdated version '{0}', minimum expected version is '{1}'.",
                  versionMessageRemote.ProtocolVersion,
                  ProtocolVersion);
              }

              if (!((ServiceFlags)versionMessageRemote.NetworkServicesLocal)
                .HasFlag(NetworkServicesRemoteRequired))
              {
                rejectionReason = string.Format(
                  "Network services '{0}' do not meet requirement '{1}'.",
                  versionMessageRemote.NetworkServicesLocal,
                  NetworkServicesRemoteRequired);
              }

              if (versionMessageRemote.UnixTimeSeconds -
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 2 * 60 * 60)
              {
                rejectionReason = string.Format(
                  "Unix time '{0}' more than 2 hours in the " +
                  "future compared to local time '{1}'.",
                  versionMessageRemote.NetworkServicesLocal,
                  NetworkServicesRemoteRequired);
              }

              if (versionMessageRemote.Nonce == Nonce)
              {
                rejectionReason = string.Format(
                  "Duplicate Nonce '{0}'.",
                  Nonce);
              }

              if (rejectionReason != "")
              {
                await SendMessage(
                  new RejectMessage(
                    "version",
                    RejectMessage.RejectCode.OBSOLETE,
                    rejectionReason));

                throw new ProtocolException(
                  "Remote peer rejected: " + rejectionReason);
              }

              await SendMessage(new VerAckMessage());
              break;

            case "reject":
              RejectMessage rejectMessage = new RejectMessage(Payload);

              throw new ProtocolException(
                string.Format("Peer rejected handshake: '{0}'",
                rejectMessage.RejectionReason));

            default:
              throw new ProtocolException(string.Format(
                "Received improper message '{0}' during handshake session.",
                Command));
          }
        }
      }

      async Task SendMessage(NetworkMessage message)
      {
        $"{GetID()} Send message {message.Command}"
          .Log(LogFile);

        NetworkStream.Write(MagicBytes, 0, MagicBytes.Length);

        byte[] command = Encoding.ASCII.GetBytes(
          message.Command.PadRight(CommandSize, '\0'));

        NetworkStream.Write(command, 0, command.Length);

        byte[] payloadLength = BitConverter.GetBytes(message.Payload.Length);
        NetworkStream.Write(payloadLength, 0, payloadLength.Length);

        byte[] checksum = CreateChecksum(
          message.Payload,
          message.Payload.Length);
        NetworkStream.Write(checksum, 0, checksum.Length);

        await NetworkStream.WriteAsync(
          message.Payload,
          0,
          message.Payload.Length)
          .ConfigureAwait(false);
      }

      byte[] CreateChecksum(byte[] payload, int count)
      {
        byte[] hash = SHA256.ComputeHash(
          SHA256.ComputeHash(payload, 0, count));

        return hash.Take(ChecksumSize).ToArray();
      }

      async Task ReadMessage(
        CancellationToken cancellationToken)
      {
        byte[] magicByte = new byte[1];

        for (int i = 0; i < MagicBytes.Length; i++)
        {
          await ReadBytes(
           magicByte,
           1,
           cancellationToken);

          if (MagicBytes[i] != magicByte[0])
          {
            i = magicByte[0] == MagicBytes[0] ? 0 : -1;
          }
        }

        await ReadBytes(
          MeassageHeader,
          MeassageHeader.Length,
          cancellationToken);

        Command = Encoding.ASCII.GetString(
          MeassageHeader.Take(CommandSize)
          .ToArray()).TrimEnd('\0');

        PayloadLength = BitConverter.ToInt32(
          MeassageHeader,
          CommandSize);

        if (PayloadLength > SIZE_MESSAGE_PAYLOAD_BUFFER)
        {
          throw new ProtocolException(string.Format(
            "Message payload too big exceeding {0} bytes.",
            SIZE_MESSAGE_PAYLOAD_BUFFER));
        }

        await ReadBytes(
          Payload,
          PayloadLength,
          cancellationToken);

        uint checksumMessage = BitConverter.ToUInt32(
          MeassageHeader, CommandSize + LengthSize);

        uint checksumCalculated = BitConverter.ToUInt32(
          CreateChecksum(
            Payload,
            PayloadLength),
          0);

        if (checksumMessage != checksumCalculated)
        {
          throw new ProtocolException("Invalid Message checksum.");
        }
      }

      async Task ReadBytes(
        byte[] buffer,
        int bytesToRead,
        CancellationToken cancellationToken)
      {
        int offset = 0;

        while (bytesToRead > 0)
        {
          int chunkSize = await NetworkStream.ReadAsync(
            buffer,
            offset,
            bytesToRead,
            cancellationToken)
            .ConfigureAwait(false);

          if (chunkSize == 0)
          {
            throw new IOException(
              "Stream returns 0 bytes signifying end of stream.");
          }

          offset += chunkSize;
          bytesToRead -= chunkSize;
        }
      }


      int CounterHeaders;

      async Task SyncToMessage()
      {
        byte[] magicByte = new byte[1];

        for (int i = 0; i < MagicBytes.Length; i++)
        {
          await ReadBytes(
           magicByte,
           1,
           Cancellation.Token);

          if (MagicBytes[i] != magicByte[0])
          {
            i = magicByte[0] == MagicBytes[0] ? 0 : -1;
          }
        }
      }

      public async Task StartMessageListener()
      {
        try
        {
          while (true)
          {
            if (FlagDispose)
            {
              return;
            }

            await SyncToMessage();

            await ReadBytes(
              MeassageHeader,
              MeassageHeader.Length,
              Cancellation.Token);

            Command = Encoding.ASCII.GetString(
              MeassageHeader.Take(CommandSize)
              .ToArray()).TrimEnd('\0');

            PayloadLength = BitConverter.ToInt32(
              MeassageHeader,
              CommandSize);
            
            if (PayloadLength > SIZE_MESSAGE_PAYLOAD_BUFFER)
            {
              throw new ProtocolException(string.Format(
                "Message payload too big exceeding {0} bytes.",
                SIZE_MESSAGE_PAYLOAD_BUFFER));
            }

            Block block = null;

            if (Command == "block")
            {
              block = BlockDownload.GetNextBlockToParse();

              await ReadBytes(
                block.Buffer,
                PayloadLength,
                Cancellation.Token);

              block.Parse();

              if (IsStateIdle())
              {
                string.Format(
                  "{0}: Receives unsolicited block {1}.",
                  GetID(),
                  block.Header.Hash.ToHexString())
                  .Log(LogFile);

                if (!Blockchain.TryLock())
                {
                  break;
                }

                Console.Beep();

                try
                {
                  if (block.Header.HashPrevious.IsEqual(
                    Blockchain.HeaderTip.Hash))
                  {
                    Blockchain.InsertBlock(
                       block,
                       flagValidateHeader: true);
                  }
                  else
                  {
                    ProcessHeaderUnsolicited(block.Header);
                  }
                }
                finally
                {
                  Blockchain.ReleaseLock();
                  Network.ClearHeaderUnsolicitedDuplicates();
                }
              }
              else if (IsStateAwaitingBlockDownload())
              {
                if (BlockDownload.InsertBlockFlagComplete(block))
                {
                  string.Format(
                    "{0}: Downloaded {1} blocks in BlockDownload {2}.",
                    GetID(),
                    BlockDownload.GetBlocks().Count,
                    BlockDownload.Index)
                    .Log(LogFile);

                  Cancellation = new CancellationTokenSource();

                  Network.InsertPeerBlockDownload(BlockDownload);

                  if (!Network.TryChargeBlockDownload(this))
                  {
                    "Synchronization ended.".Log(LogFile);

                    Release();
                    break;
                  }

                  await GetBlock();
                }
                else
                {
                  Cancellation.CancelAfter(
                    TIMEOUT_RESPONSE_MILLISECONDS);
                }
              }
            }
            else
            {
              await ReadBytes(
                Payload,
                PayloadLength,
                Cancellation.Token);

              uint checksumMessage = BitConverter.ToUInt32(
                MeassageHeader, CommandSize + LengthSize);

              uint checksumCalculated = BitConverter.ToUInt32(
                CreateChecksum(
                  Payload,
                  PayloadLength),
                0);

              if (checksumMessage != checksumCalculated)
              {
                throw new ProtocolException("Invalid Message checksum.");
              }

              switch (Command)
              {
                case "ping":
                  await SendMessage(new PongMessage(
                    BitConverter.ToUInt64(Payload, 0)));
                  break;

                case "addr":
                  AddressMessage addressMessage =
                    new AddressMessage(Payload);
                  break;

                case "sendheaders":
                  await SendMessage(new SendHeadersMessage());

                  break;

                case "feefilter":
                  FeeFilterMessage feeFilterMessage = new(Payload);
                  FeeFilterValue = feeFilterMessage.FeeFilterValue;
                  break;

                case "headers":

                  Header header = null;
                  int byteIndex = 0;

                  int countHeaders = VarInt.GetInt32(
                    Payload,
                    ref byteIndex);

                  if (IsStateIdle())
                  {
                    header = Token.ParseHeader(
                      Payload,
                      ref byteIndex,
                      SHA256);

                    string.Format(
                      "Received unsolicited header {0}",
                      header.Hash.ToHexString())
                      .Log(LogFile);

                    if (Network.IsHeaderUnsolicitedDuplicate(header))
                    {
                      break;
                    }

                    if (!Blockchain.TryLock())
                    {
                      break;
                    }

                    try
                    {
                      if (header.HashPrevious.IsEqual(
                        Blockchain.HeaderTip.Hash))
                      {
                        await SendMessage(new GetDataMessage(
                          new Inventory(
                            InventoryType.MSG_BLOCK,
                            header.Hash)));
                      }
                      else
                      {
                        ProcessHeaderUnsolicited(header);
                      }
                    }
                    finally
                    {
                      Blockchain.ReleaseLock();
                    }
                  }
                  else if (IsStateGetHeaders())
                  {
                    $"{GetID()}: Receiving {countHeaders} headers."
                      .Log(LogFile);

                    while (byteIndex < PayloadLength)
                    {
                      header = Token.ParseHeader(
                        Payload,
                        ref byteIndex,
                        SHA256);

                      byteIndex += 1;

                      HeaderDownload.InsertHeader(header, Token);
                    }

                    CounterHeaders += countHeaders;
                    if (CounterHeaders > 10000)
                    {
                      Network.SynchronizeBlocks(this);
                      break;
                    }

                    if (countHeaders == 0)
                    {
                      if (HeaderDownload.HeaderTip == null)
                      {
                        lock (this)
                        {
                          State = StateProtocol.IDLE;
                          IsSynchronized = true;
                        }

                        Blockchain.ReleaseLock();

                        break;
                      }

                      if (HeaderDownload.IsFork)
                      {
                        if (!await Blockchain.TryFork(
                           HeaderDownload.HeaderLocatorAncestor))
                        {
                          Release();
                        }
                      }

                      Network.SynchronizeBlocks(this);
                    }
                    else
                    {
                      Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

                      await SendMessage(new GetHeadersMessage(
                        new List<Header> { HeaderDownload.HeaderInsertedLast },
                        ProtocolVersion));
                    }
                  }

                  break;

                case "notfound":

                  "Received meassage notfound.".Log(LogFile);

                  if (IsStateAwaitingBlockDownload())
                  {
                    Network.ReturnPeerBlockDownloadIncomplete(this);

                    lock (this)
                    {
                      State = StateProtocol.IDLE;
                    }

                    if (this == Network.PeerSynchronizationMaster)
                    {
                      throw new ProtocolException(
                        $"Peer has sent headers but does not deliver blocks.");
                    }
                  }

                  break;

                case "inv":

                  InvMessage invMessage = new(Payload);

                  GetDataMessage getDataMessage =
                    new(invMessage.Inventories);

                  break;

                case "getdata":

                  getDataMessage = new GetDataMessage(Payload);

                  foreach (Inventory inventory in getDataMessage.Inventories)
                  {
                    if (inventory.Type == InventoryType.MSG_TX)
                    {
                      if (Token.TryRequestTX(
                        inventory.Hash,
                        out byte[] tXRaw))
                      {
                        await SendMessage(new TXMessage(tXRaw));

                        string.Format(
                          "{0} received getData {1} and sent tXMessage {2}.",
                          GetID(),
                          getDataMessage.Inventories[0].Hash.ToHexString(),
                          inventory.Hash.ToHexString())
                          .Log(LogFile);
                      }
                      else
                      {
                        // Send notfound
                      }
                    }
                  }

                  break;

                default:
                  //string.Format(
                  //  "{0} received unknown message {1}.",
                  //  GetID(),
                  //  Command)
                  //  .Log(LogFile);
                  break;
              }
            }

            //string.Format(
            //  "{0} received message {1}",
            //  GetID(),
            //  Command)
            //  .Log(LogFile);

          }
        }
        catch (Exception ex)
        {
          SetFlagDisposed(
            $"{ex.GetType().Name} in listener: \n{ex.Message}");

          if (IsStateAwaitingHeader())
          {
            Blockchain.ReleaseLock();
          }
          else if (IsStateAwaitingBlockDownload())
          {
            Network.ReturnPeerBlockDownloadIncomplete(this);
          }
        }
      }


      
      internal Header HeaderDuplicateReceivedLast;
      internal int CountOrphanReceived;

      void ProcessHeaderUnsolicited(Header header)
      {
        if (Blockchain.TryReadHeader(
          header.Hash,
          out Header headerReceivedNow))
        {
          if (HeaderDuplicateReceivedLast != null &&
            HeaderDuplicateReceivedLast.Height >= headerReceivedNow.Height)
          {
            throw new ProtocolException(string.Format(
              "Sent duplicate block {0}",
              header.Hash.ToHexString()));
          }

          HeaderDuplicateReceivedLast = headerReceivedNow;
        }
        else
        {
          if (IsSynchronized)
          {
            CountOrphanReceived = 0;
            IsSynchronized = false;
          }

          if (CountOrphanReceived > 10)
          {
            throw new ProtocolException(
              "Too many orphan blocks or headers received.");
          }

          CountOrphanReceived += 1;
        }
      }

      public async Task AdvertizeToken(byte[] hash)
      {
        string.Format(
          "{0} advertize token {1}.",
          GetID(),
          hash.ToHexString())
          .Log(LogFile);

        var inventoryTX = new Inventory(
          InventoryType.MSG_TX,
          hash);

        var invMessage = new InvMessage(
          new List<Inventory> { inventoryTX });

        await SendMessage(invMessage);

        Release();
      }
         
           

      /// <summary>
      /// Request all headers from peer returning the root header.
      /// </summary>
      public async Task GetHeaders()
      {
        HeaderDownload.Reset();
        HeaderDownload.Locator = Blockchain.GetLocator();
        List<Header> headerLocatorNext = HeaderDownload.Locator.ToList();

        string.Format(
          "Send getheaders to peer {0}, \n" +
          "locator: {1} ... \n{2}",
          GetID(),
          headerLocatorNext.First().Hash.ToHexString(),
          headerLocatorNext.Count > 1 ? headerLocatorNext.Last().Hash.ToHexString() : "")
          .Log(LogFile);

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetHeadersMessage(
          headerLocatorNext,
          ProtocolVersion));
      }

      public async Task GetBlock()
      {
        List<Inventory> inventories =
          BlockDownload.HeadersExpected.Select(
            h => new Inventory(
              InventoryType.MSG_BLOCK,
              h.Hash))
              .ToList();

        Cancellation.CancelAfter(
            TIMEOUT_RESPONSE_MILLISECONDS);

        BlockDownload.StopwatchBlockDownload.Restart();

        await SendMessage(new GetDataMessage(inventories));

        lock (this)
        {
          State = StateProtocol.AwaitingBlockDownload;
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
          if (
            FlagDispose ||
            IsBusy)
          {
            return false;
          }

          IsBusy = true;
          return true;
        }
      }

      public bool TryStageSynchronization()
      {
        lock(this)
        {
          if (
            IsSynchronized ||
            FlagDispose ||
            IsBusy)
          {
            return false;
          }

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
        {
          return State == StateProtocol.IDLE;
        }
      }

      public void SetStateAwaitingHeader()
      {
        lock (this)
        {
          State = StateProtocol.AwaitingHeader;
        }
      }

      bool IsStateGetHeaders()
      {
        lock (this)
        {
          return State == StateProtocol.AwaitingHeader;
        }
      }

      bool IsStateAwaitingGetDataTX()
      {
        lock (this)
        {
          return State == StateProtocol.AwaitingGetData;
        }
      }

      public bool IsStateAwaitingHeader()
      {
        lock (this)
        {
          return State == StateProtocol.AwaitingHeader;
        }
      }

      public bool IsStateAwaitingBlockDownload()
      {
        lock (this)
        {
          return State == StateProtocol.AwaitingBlockDownload;
        }
      }

      public bool IsInbound()
      {
        return Connection == ConnectionType.INBOUND;
      }

      public string GetID()
      {
        return IPAddress.ToString();
      }

      public void SetFlagDisposed(string message)
      {
        $"Set flag dispose on peer {GetID()}: {message}"
          .Log(LogFile);

        FlagDispose = true;
      }

      public void Dispose()
      {
        string.Format(
          "Dispose peer {0}.",
          GetID()).Log(LogFile);

        Cancellation.Cancel();

        TcpClient.Dispose();

        LogFile.Dispose();

        File.Move(
          Path.Combine(
            DirectoryLogPeers.FullName,
            GetID()),
          Path.Combine(
            DirectoryLogPeersDisposed.FullName,
            GetID()));
      }
    }
  }
}
