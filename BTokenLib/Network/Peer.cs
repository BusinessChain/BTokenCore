﻿using System;
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
      Stopwatch StopwatchDownload = new();
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

      public async Task SendMessage(NetworkMessage message)
      {
        string.Format(
          "{0} Send message {1}",
          GetID(),
          message.Command)
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

      readonly object LOCK_StateProtocol = new object();
      enum StateProtocol
      {
        IDLE = 0,
        AwaitingBlockDownload,
        GetHeader,
        AwaitingGetData
      }

      StateProtocol State;
      BufferBlock<bool> SignalProtocolTaskCompleted = new();

      public async Task StartMessageListener()
      {
        try
        {
          while (true)
          {
            await ReadMessage(Cancellation.Token);

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

                byte[] blockBytes = Payload
                  .Take(PayloadLength)
                  .ToArray();

                Block block = ParserToken.ParseBlock(blockBytes);

                if (IsStateIdle())
                {
                  string.Format(
                    "{0}: Receives unsolicited block {1}.",
                    GetID(),
                    block.Header.Hash.ToHexString())
                    .Log(LogFile);

                  Console.Beep();

                  if (!Blockchain.TryLock())
                  {
                    break;
                  }

                  try
                  {
                    ProcessHeaderUnsolicited(
                      block.Header,
                      out bool flagHeaderExtendsChain);

                    if(flagHeaderExtendsChain)
                    {
                      if(!Blockchain.TryInsertBlock(
                        block,
                        flagValidateHeader: true))
                      {
                        // Blockchain insert sollte doch einfach Ex. schmeissen
                      }
                    }  
                  }
                  catch (Exception ex)
                  {
                    Blockchain.ReleaseLock();

                    throw ex;
                  }

                  Blockchain.ReleaseLock();
                }
                else if (IsStateAwaitingBlockDownload())
                {
                  string.Format(
                    "{0}: Receives awaited block {1}.",
                    GetID(),
                    block.Header.Hash.ToHexString())
                    .Log(LogFile);

                  BlockDownload.InsertBlock(block);

                  if (BlockDownload.IsDownloadCompleted)
                  {
                    SignalProtocolTaskCompleted.Post(true);

                    lock (LOCK_StateProtocol)
                    {
                      State = StateProtocol.IDLE;
                    }

                    break;
                  }

                  Cancellation.CancelAfter(
                    TIMEOUT_RESPONSE_MILLISECONDS);
                }

                break;

              case "headers":

                Header header = null;
                int index = 0;

                int countHeaders = VarInt.GetInt32(
                  Payload,
                  ref index);

                string.Format(
                  "{0}: Receiving {1} headers.",
                  GetID(),
                  countHeaders)
                  .Log(LogFile);

                if (IsStateIdle())
                {
                  header = Token.ParseHeader(
                    Payload,
                    ref index);

                  string.Format(
                    "Received unsolicited header {0}",
                    header.Hash.ToHexString())
                    .Log(LogFile);

                  index += 1;

                  if (!Blockchain.TryLock())
                  {
                    break;
                  }

                  try
                  {
                    ProcessHeaderUnsolicited(
                      header,
                      out bool flagHeaderExtendsChain);

                    if (flagHeaderExtendsChain)
                    {
                      List<Inventory> inventories = new()
                      {
                        new Inventory(
                          InventoryType.MSG_BLOCK,
                          header.Hash)
                      };

                      SendMessage(new GetDataMessage(inventories));
                    }
                  }
                  catch (Exception ex)
                  {
                    Blockchain.ReleaseLock();

                    throw ex;
                  }

                  Blockchain.ReleaseLock();
                }
                else if (IsStateGetHeaders())
                {
                  if (countHeaders > 0)
                  {
                    while (index < PayloadLength)
                    {
                      header = Token.ParseHeader(
                        Payload,
                        ref index);

                      index += 1;

                      HeaderDownload.InsertHeader(header, Token);
                    }
                  }

                  SignalProtocolTaskCompleted.Post(true);
                }

                break;

              case "notfound":

                string.Format(
                  "Received meassage notfound.")
                  .Log(LogFile);

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
                      SendMessage(new TXMessage(tXRaw));

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
                string.Format(
                  "{0} received unknown message {1}.",
                  GetID(),
                  Command)
                  .Log(LogFile);
                break;
            }
          }
        }
        catch (Exception ex)
        {
          Cancellation.Cancel();

          SetFlagDisposed(string.Format(
            "{0} in listener.: \n{1}",
            ex.GetType().Name,
            ex.Message));
        }
      }

      /// <summary>
      /// Check if header is duplicate. Check if header extends chain, 
      /// otherwise mark peer not synchronized.
      /// </summary>
      void ProcessHeaderUnsolicited(
        Header header, 
        out bool flagHeaderExtendsChain)
      {
        flagHeaderExtendsChain = false;

        if (Blockchain.ContainsHeader(header.Hash))
        {
          Header headerContained = Blockchain.HeaderTip;

          List<byte[]> headerDuplicates = new();
          int depthDuplicateAcceptedMax = 3;
          int depthDuplicate = 0;

          while (depthDuplicate < depthDuplicateAcceptedMax)
          {
            if (headerContained.Hash.IsEqual(header.Hash))
            {
              if (headerDuplicates.Any(h => h.IsEqual(header.Hash)))
              {
                throw new ProtocolException(
                  string.Format(
                    "Received duplicate header {0} more than once.",
                    header.Hash.ToHexString()));
              }

              headerDuplicates.Add(header.Hash);
              if (headerDuplicates.Count > depthDuplicateAcceptedMax)
              {
                headerDuplicates = headerDuplicates.Skip(1).ToList();
              }

              break;
            }

            if (headerContained.HeaderPrevious != null)
            {
              break;
            }

            headerContained = header.HeaderPrevious;
            depthDuplicate += 1;
          }

          if (depthDuplicate == depthDuplicateAcceptedMax)
          {
            throw new ProtocolException(
              string.Format(
                "Received duplicate header {0} with depth greater than {1}.",
                header.Hash.ToHexString(),
                depthDuplicateAcceptedMax));
          }
        }
        else if (header.HashPrevious.IsEqual(
          Blockchain.HeaderTip.Hash))
        {
          flagHeaderExtendsChain = true;
        }
        else
        {
          IsSynchronized = false;
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

        Network.ReleasePeer(this);
      }




      public async Task Synchronize()
      {
      LABEL_SynchronizeWithPeer:

        await GetHeaders();

        if (HeaderDownload.HeaderTip == null)
        {
          return;
        }

        Dictionary<int, BlockDownload> downloadsAwaiting = new();
        List<BlockDownload> queueDownloadsIncomplete = new();
        BufferBlock<Peer> queueSynchronizer = new();

        List<Peer> peersRetired = new();
        List<Peer> peersDownloading = new();
        bool flagAbort = false;

        int indexBlockDownload = 0;
        int indexInsertion = 0;

        Header headerRoot = HeaderDownload.HeaderRoot;
        double difficultyOld = Blockchain.HeaderTip.Difficulty;

        if (HeaderDownload.IsFork)
        {
          if (!await Blockchain.TryFork(
             HeaderDownload.HeaderLocatorAncestor.Height,
             headerRoot.HashPrevious))
          {
            goto LABEL_SynchronizeWithPeer;
          }
        }

        Peer peer = this;

        while (true)
        {
          do
          {
            bool isBlockDownloadAvailable =
              queueDownloadsIncomplete.Count > 0 || headerRoot != null;

            if (
             flagAbort ||
             peer.FlagDispose ||
             !isBlockDownloadAvailable)
            {
              if (peer != this)
              {
                Network.ReleasePeer(peer);
              }

              if (peersDownloading.Count == 0)
              {
                string.Format(
                  "Synchronization ended {0}.",
                  flagAbort ? "unsuccessfully" : "successfully").
                  Log(LogFile);

                if (flagAbort)
                {
                  await Blockchain.LoadImage();
                }

                if (Blockchain.IsFork)
                {
                  if (Blockchain.HeaderTip.Difficulty > difficultyOld)
                  {
                    Blockchain.Reorganize();
                  }
                  else
                  {
                    Blockchain.DismissFork();
                    await Blockchain.LoadImage();
                  }
                }

                return;
              }

              break;
            }
            else
            {
              if (queueDownloadsIncomplete.Any())
              {
                peer.BlockDownload = queueDownloadsIncomplete.First();
                queueDownloadsIncomplete.RemoveAt(0);

                peer.BlockDownload.Peer = peer;

                string.Format(
                  "peer {0} loads blockDownload {1} from queue invalid.",
                  peer.GetID(),
                  peer.BlockDownload.Index)
                  .Log(LogFile);
              }
              else
              {
                peer.BlockDownload = new BlockDownload(
                  indexBlockDownload,
                  peer);

                peer.BlockDownload.LoadHeaders(
                  ref headerRoot,
                  peer.CountBlocksLoad);

                indexBlockDownload += 1;
              }

              peersDownloading.Add(peer);

              RunBlockDownload(peer, queueSynchronizer);
            }
          } while (
          downloadsAwaiting.Count < 10 &&
          Network.TryGetPeer(out peer));

          peer = await queueSynchronizer
            .ReceiveAsync()
            .ConfigureAwait(false);

          peersDownloading.Remove(peer);

          var blockDownload = peer.BlockDownload;
          peer.BlockDownload = null;

          if (flagAbort)
          {
            continue;
          }

          if (!blockDownload.IsDownloadCompleted)
          {
            EnqueueBlockDownloadIncomplete(
              blockDownload,
              queueDownloadsIncomplete);

            if (peer == this)
            {
              SetFlagDisposed(
                string.Format(
                  "Block download {0} not complete.",
                  blockDownload.Index));
            }

            if (!peer.FlagDispose)
            {
              peersRetired.Add(peer);
            }
          }
          else
          {
            if (indexInsertion == blockDownload.Index)
            {
              while (true)
              {
                if (!TryInsertBlockDownload(blockDownload))
                {
                  blockDownload.Peer.SetFlagDisposed(
                    string.Format(
                      "Insertion of block download {0} failed. " +
                      "Abort flag is set.",
                      blockDownload.Index));

                  flagAbort = true;

                  break;
                }

                string.Format(
                  "Insert block download {0}. Blockchain height: {1}",
                  blockDownload.Index,
                  Blockchain.HeaderTip.Height)
                  .Log(LogFile);

                indexInsertion += 1;

                if (!downloadsAwaiting.TryGetValue(
                  indexInsertion,
                  out blockDownload))
                {
                  break;
                }

                downloadsAwaiting.Remove(indexInsertion);
              }
            }
            else
            {
              downloadsAwaiting.Add(
                blockDownload.Index,
                blockDownload);
            }
          }
        }
      }

      static async Task RunBlockDownload(
      Peer peer,
      BufferBlock<Peer> queueSynchronizer)
      {
        await peer.DownloadBlocks();
        queueSynchronizer.Post(peer);
      }

      static void EnqueueBlockDownloadIncomplete(
        BlockDownload download,
        List<BlockDownload> queue)
      {
        download.IndexHeaderExpected = 0;
        download.Blocks.Clear();

        int indexdownload = queue
          .FindIndex(b => b.Index > download.Index);

        if (indexdownload == -1)
        {
          queue.Add(download);
        }
        else
        {
          queue.Insert(
            indexdownload,
            download);
        }
      }

      bool TryInsertBlockDownload(BlockDownload blockDownload)
      {
        foreach (Block block in blockDownload.Blocks)
        {
          if (!Blockchain.TryInsertBlock(
              block,
              flagValidateHeader: false))
          {
            return false;
          }

          Blockchain.ArchiveBlock(
              block,
              UTXOIMAGE_INTERVAL_SYNC);
        }

        return true;
      }


      /// <summary>
      /// Request all headers from peer returning the root header.
      /// </summary>
      public async Task GetHeaders()
      {
        HeaderDownload.Reset();
        HeaderDownload.Locator = Blockchain.GetLocator();
        List<Header> headerLocatorNext = HeaderDownload.Locator.ToList();
        int countHeaderOld;

        CancellationTokenSource cancelGetHeaders = new();

        do
        {
          countHeaderOld = HeaderDownload.CountHeaders;

          if(HeaderDownload.HeaderInsertedLast != null)
          {
            headerLocatorNext.Clear();
            headerLocatorNext.Add(HeaderDownload.HeaderInsertedLast);
          }

          string.Format(
            "Send getheaders to peer {0}, \n" +
            "locator: {1} ... \n{2}",
            GetID(),
            headerLocatorNext.First().Hash.ToHexString(),
            headerLocatorNext.Count > 1 ? headerLocatorNext.Last().Hash.ToHexString() : "")
            .Log(LogFile);

          await SendMessage(new GetHeadersMessage(
            headerLocatorNext,
            ProtocolVersion));

          lock (LOCK_StateProtocol)
          {
            State = StateProtocol.GetHeader;
          }

          cancelGetHeaders.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

          await SignalProtocolTaskCompleted
            .ReceiveAsync(cancelGetHeaders.Token)
            .ConfigureAwait(false);

        } while (HeaderDownload.CountHeaders > countHeaderOld);

        lock (LOCK_StateProtocol)
        {
          State = StateProtocol.IDLE;
        }
      }

      public async Task DownloadBlocks()
      {
        StopwatchDownload.Restart();

        try
        {
          List<Inventory> inventories =
            BlockDownload.HeadersExpected.Select(
              h => new Inventory(
                InventoryType.MSG_BLOCK,
                h.Hash))
                .ToList();

          await SendMessage(new GetDataMessage(inventories));

          lock (LOCK_StateProtocol)
          {
            State = StateProtocol.AwaitingBlockDownload;
          }

          Cancellation.CancelAfter(
              TIMEOUT_RESPONSE_MILLISECONDS);

          await SignalProtocolTaskCompleted
            .ReceiveAsync(Cancellation.Token)
            .ConfigureAwait(false);

          Cancellation = new CancellationTokenSource();
        }
        catch (Exception ex)
        {
          SetFlagDisposed(string.Format(
            "{0} when downloading block download {1}.: \n{2}",
            ex.GetType().Name,
            BlockDownload.Index,
            ex.Message));

          return;
        }

        AdjustCountBlocksLoad();

        string.Format(
          "{0}: Downloaded {1} blocks in download {2} in {3} ms.",
          GetID(),
          BlockDownload.Blocks.Count,
          BlockDownload.Index,
          StopwatchDownload.ElapsedMilliseconds)
          .Log(LogFile);
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

      bool IsStateIdle()
      {
        lock (LOCK_StateProtocol)
        {
          return State == StateProtocol.IDLE;
        }
      }

      bool IsStateGetHeaders()
      {
        lock (LOCK_StateProtocol)
        {
          return State == StateProtocol.GetHeader;
        }
      }

      bool IsStateAwaitingGetDataTX()
      {
        lock (LOCK_StateProtocol)
        {
          return State == StateProtocol.AwaitingGetData;
        }
      }

      bool IsStateAwaitingBlockDownload()
      {
        lock (LOCK_StateProtocol)
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
        string.Format(
          "Set flag dispose on peer {0}: {1}",
          GetID(),
          message).Log(LogFile);

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
