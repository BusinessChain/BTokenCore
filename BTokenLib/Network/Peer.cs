using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
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
      Token.IParser ParserToken;


      public bool IsBusy;

      public bool FlagDispose;
      public bool IsSynchronized;

      const int TIMEOUT_RESPONSE_MILLISECONDS = 5000;

      const int COUNT_BLOCKS_DOWNLOADBATCH_INIT = 1;
      public Stopwatch StopwatchDownload = new();
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

      enum StateProtocol
      {
        IDLE = 0,
        AwaitingBlockDownload,
        GetHeader,
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
      {
        Network = network;
        Blockchain = blockchain;
        Token = token;
        ParserToken = token.CreateParser();

        TcpClient = tcpClient;

        NetworkStream = tcpClient.GetStream();

        Connection = ConnectionType.INBOUND;

        CreateLogFile();
      }

      public Peer(
        Network network,
        Blockchain blockchain,
        Token token,
        IPAddress iPAddress)
      {
        Network = network;
        Blockchain = blockchain;
        Token = token;
        ParserToken = token.CreateParser();

        Connection = ConnectionType.OUTBOUND;
        IPAddress = iPAddress;

        CreateLogFile();
      }


      void CreateLogFile()
      {
        PathLogFile = Path.Combine(
           DirectoryLogPeers.Name,
          GetID());

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
              "Cannot create logfile file peer {0}: {1}",
              GetID(),
              ex.Message);

            Thread.Sleep(10000);
          }
        }
      }

      public async Task Connect(int port)
      {
        string.Format(
          "Connect peer {0}",
          GetID())
          .Log(LogFile);

        TcpClient = new TcpClient();

        await TcpClient.ConnectAsync(
          IPAddress,
          port);

        NetworkStream = TcpClient.GetStream();

        await HandshakeAsync(port);

        string.Format(
          "Network protocol handshake {0}",
          GetID())
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

      StateProtocol State;

      public async Task StartMessageListener()
      {
        try
        {
          while (true)
          {
            await ReadMessage(Cancellation.Token);

            if(FlagDispose)
            {
              return;
            }

            //string.Format(
            //  "{0} received message {1}",
            //  GetID(),
            //  Command)
            //  .Log(LogFile);

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

              case "block":

                Block block = ParserToken.ParseBlock(Payload);

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
                  BlockDownload.InsertBlock(block);

                  if (BlockDownload.IsDownloadCompleted)
                  {
                    Network.InsertBlockDownload(this);

                    Cancellation = new CancellationTokenSource();

                    AdjustCountBlocksLoad();

                    string.Format(
                      "{0}: Downloaded {1} blocks in download {2} in {3} ms.",
                      GetID(),
                      BlockDownload.Blocks.Count,
                      BlockDownload.Index,
                      StopwatchDownload.ElapsedMilliseconds)
                      .Log(LogFile);

                    lock (this)
                    {
                      State = StateProtocol.IDLE;
                    }
                  }
                  else
                  {
                    Cancellation.CancelAfter(
                      TIMEOUT_RESPONSE_MILLISECONDS);
                  }
                }

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
                    ref byteIndex);

                  string.Format(
                    "Received unsolicited header {0}",
                    header.Hash.ToHexString())
                    .Log(LogFile);

                  if(Network.IsHeaderUnsolicitedDuplicate(header))
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
                      ref byteIndex);

                    byteIndex += 1;

                    HeaderDownload.InsertHeader(header, Token);
                  }

                  if(countHeaders == 0)
                  {
                    if (HeaderDownload.HeaderTip == null)
                    {
                      lock (this)
                      {
                        State = StateProtocol.IDLE;
                        IsBusy = false;
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

                    Network.Synchronize(this);
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
                  Network.PeerBlockDownloadIncomplete(this);

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

                foreach(Inventory inventory in getDataMessage.Inventories)
                {
                  if(inventory.Type == InventoryType.MSG_TX)
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
        }
        catch (Exception ex)
        {
          SetFlagDisposed(
            $"{ex.GetType().Name} in listener: \n{ex.Message}");

          if (IsStateAwaitingBlockDownload())
          {
            Network.PeerBlockDownloadIncomplete(this);
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

        lock (this)
        {
          State = StateProtocol.GetHeader;
        }
      }

      public async Task GetBlock()
      {
        StopwatchDownload.Restart();

        List<Inventory> inventories =
          BlockDownload.HeadersExpected.Select(
            h => new Inventory(
              InventoryType.MSG_BLOCK,
              h.Hash))
              .ToList();

        Cancellation.CancelAfter(
            TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetDataMessage(inventories));

        lock (this)
        {
          State = StateProtocol.AwaitingBlockDownload;
        }
      }

      void AdjustCountBlocksLoad()
      {
        var ratio = TIMEOUT_RESPONSE_MILLISECONDS /
          (double)StopwatchDownload.ElapsedMilliseconds - 1;

        int correctionTerm = (int)(CountBlocksLoad * ratio);

        if (correctionTerm > 10)
        {
          correctionTerm = 10;
        }

        CountBlocksLoad = Math.Min(
          CountBlocksLoad + correctionTerm,
          500);

        CountBlocksLoad = Math.Max(CountBlocksLoad, 1);
      }



      public async Task SendHeaders(List<Header> headers)
      {
        await SendMessage(new HeadersMessage(headers));
      }


      public void Release()
      {
        lock (this)
        {
          IsBusy = false;
          State = StateProtocol.IDLE;
        }
      }

      public bool IsReady()
      {
        lock(this)
        {
          return !FlagDispose && !IsBusy;
        }
      }

      bool IsStateIdle()
      {
        lock (this)
        {
          return State == StateProtocol.IDLE;
        }
      }

      bool IsStateGetHeaders()
      {
        lock (this)
        {
          return State == StateProtocol.GetHeader;
        }
      }

      bool IsStateAwaitingGetDataTX()
      {
        lock (this)
        {
          return State == StateProtocol.AwaitingGetData;
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
