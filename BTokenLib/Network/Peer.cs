﻿using System;
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
      Blockchain Blockchain;
      Token Token;

      public bool IsBusy;
      public bool FlagDispose;
      public bool FlagSynchronizationScheduled;

      public enum StateProtocol
      {
        IDLE = 0,
        AwaitingBlockDownload,
        AwaitingHeader,
        AwaitingGetData
      }
      public StateProtocol State;

      internal HeaderDownload HeaderDownload;
      internal BlockDownload BlockDownload;
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

      const int SIZE_MESSAGE_PAYLOAD_BUFFER = 0x400000;
      byte[] Payload = new byte[SIZE_MESSAGE_PAYLOAD_BUFFER];
      int PayloadLength;

      const int HeaderSize = CommandSize + LengthSize + ChecksumSize;
      byte[] MeassageHeader = new byte[HeaderSize];
      byte[] MagicBytes = new byte[4] { 0xF9, 0xBE, 0xB4, 0xD9 };

      SHA256 SHA256 = SHA256.Create();

      StreamWriter LogFile;
      string PathLogFile;

      public int CountStartBlockDownload;
      public int CountInsertBlockDownload;
      public int CountWastedBlockDownload;
      public int CountBlockingBlockDownload;

      long TimeCreation = DateTimeOffset.UtcNow.ToUnixTimeSeconds();



      public Peer(
        Network network,
        Blockchain blockchain,
        Token token,
        TcpClient tcpClient)
        : this(
           network,
           blockchain,
           token,
           ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address)
      {
        TcpClient = tcpClient;
        NetworkStream = tcpClient.GetStream();
        Connection = ConnectionType.INBOUND;
      }

      public Peer(
        Network network,
        Blockchain blockchain,
        Token token,
        IPAddress ip)
      {
        Network = network;
        Blockchain = blockchain;
        Token = token;

        IPAddress = ip;
        Connection = ConnectionType.OUTBOUND;
        FlagSynchronizationScheduled = true;

        CreateLogFile(ip.ToString());

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
              $"Cannot create logfile file peer {this}: {ex.Message}");

            Thread.Sleep(10000);
          }
        }
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
          blockchainHeight: Blockchain.HeaderTip.Height,
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

      async Task SyncToMessage()
      {
        byte[] magicByte = new byte[1];

        for (int i = 0; i < MagicBytes.Length; i++)
        {
          await ReadBytes(magicByte, 1);

          if (MagicBytes[i] != magicByte[0])
            i = magicByte[0] == MagicBytes[0] ? 0 : -1;
        }
      }


      internal Header HeaderDuplicateReceivedLast;
      internal int CountOrphanReceived;

      int IndexBlockArchiveCache = -1;
      byte[] CacheBlockArchive;

      public async Task StartMessageListener()
      {
        try
        {
          while (true)
          {
            if (FlagDispose) 
              return;

            await SyncToMessage();

            await ReadBytes(
              MeassageHeader,
              MeassageHeader.Length);

            Command = Encoding.ASCII.GetString(
              MeassageHeader.Take(CommandSize)
              .ToArray()).TrimEnd('\0');

            PayloadLength = BitConverter.ToInt32(
              MeassageHeader,
              CommandSize);
            
            if (PayloadLength > SIZE_MESSAGE_PAYLOAD_BUFFER)
              throw new ProtocolException(
                $"Message payload too big exceeding " +
                $"{SIZE_MESSAGE_PAYLOAD_BUFFER} bytes.");

            if (Command == "block")
            {
              if (BlockDownload == null &&
                !Network.PoolBlockDownload.TryTake(out BlockDownload))
              {
                BlockDownload = new(Token);
              }

              Block block = BlockDownload.GetBlockToParse();

              await ReadBytes(block.Buffer, PayloadLength);

              if (IsStateIdle())
              {
                block.Parse();

                if (await Network.EnqueuBlockUnsolicitedFlagReject(block.Header))
                  break;

                Console.Beep();

                try
                {
                  if (block.Header.HashPrevious.IsEqual(
                    Blockchain.HeaderTip.Hash))
                  {
                    Blockchain.InsertBlock(block);

                    Network.RelayBlock(block, this);

                    $"{this}: Inserted unsolicited block {block}."
                      .Log(LogFile);
                  }
                  else
                    ProcessHeaderUnsolicited(block.Header);
                }
                catch (ProtocolException ex)
                {
                  SetFlagDisposed(ex.Message);
                }
                finally
                {
                  Blockchain.ReleaseLock();
                }
              }
              else if (IsStateAwaitingBlockDownload())
              {
                BlockDownload.Parse();

                if (BlockDownload.IsComplete())
                {
                  Cancellation = new();

                  if (Network.InsertBlockDownloadFlagContinue(this))
                  {
                    await GetBlock();
                    continue;
                  }

                  Release();
                }
              }
            }
            else
            {
              await ReadBytes(Payload, PayloadLength);

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
                  int byteIndex = 0;

                  int countHeaders = VarInt.GetInt32(
                    Payload,
                    ref byteIndex);

                  if (IsStateIdle())
                  {
                    HeaderUnsolicited = Token.ParseHeader(
                      Payload,
                      ref byteIndex,
                      SHA256);

                    Network.ThrottleDownloadBlockUnsolicited();

                    Console.Beep();

                    try
                    {
                      if (HeaderUnsolicited.HashPrevious.IsEqual(
                        Blockchain.HeaderTip.Hash))
                      {
                        await SendMessage(new GetDataMessage(
                          new List<Inventory>()
                          {
                            new Inventory(
                              InventoryType.MSG_BLOCK,
                              HeaderUnsolicited.Hash)
                          }));

                        ($"{this}: Requested block for unsolicited " +
                          $"header {HeaderUnsolicited}.")
                          .Log(LogFile);
                      }
                      else
                        ProcessHeaderUnsolicited(HeaderUnsolicited);
                    }
                    catch (ProtocolException ex)
                    {
                      SetFlagDisposed(ex.Message);
                    }
                  }
                  else if (IsStateGetHeaders())
                  {
                    $"{this}: Receiving {countHeaders} headers."
                      .Log(LogFile);

                    while (byteIndex < PayloadLength)
                    {
                      Header header = Token.ParseHeader(
                        Payload,
                        ref byteIndex,
                        SHA256);

                      byteIndex += 1;

                      HeaderDownload.InsertHeader(header, Token);
                    }

                    if (countHeaders == 0)
                    {
                      Cancellation = new();

                      Network.Synchronize();
                    }
                    else
                    {
                      await SendMessage(new GetHeadersMessage(
                        new List<Header> { HeaderDownload.HeaderInsertedLast },
                        ProtocolVersion));

                      Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);
                    }
                  }

                  break;

                case "getheaders":
                  byte[] hashHeaderAncestor = new byte[32];

                  int startIndex = 4;

                  int headersCount = VarInt.GetInt32(Payload, ref startIndex);

                  int i = 0;
                  List<Header> headers = new();

                  while (i < headersCount)
                  {
                    i += 1;

                    Array.Copy(Payload, startIndex, hashHeaderAncestor, 0, 32);
                    startIndex += 32;

                    if (Blockchain.TryReadHeader(
                      hashHeaderAncestor,
                      out Header header))
                    {
                      while (header.HeaderNext != null && headers.Count < 2000)
                      {
                        headers.Add(header.HeaderNext);
                        header = header.HeaderNext;
                      }

                      await SendHeaders(headers);

                      break;
                    }
                    else if (i == headersCount)
                    {
                      await SendHeaders(headers);

                      FlagSynchronizationScheduled = true;
                    }
                  }

                  break;

                case "notfound":

                  "Received meassage notfound.".Log(LogFile);

                  if (IsStateAwaitingBlockDownload())
                  {
                    Network.ReturnPeerBlockDownloadIncomplete(this);

                    lock (this)
                      State = StateProtocol.IDLE;

                    if (this == Network.PeerSynchronization)
                      throw new ProtocolException(
                        $"Peer has sent headers but does not deliver blocks.");
                  }

                  break;

                case "inv":

                  InvMessage invMessage = new(Payload);
                  GetDataMessage getDataMessage = new(invMessage.Inventories);

                  break;

                case "getdata":

                  getDataMessage = new(Payload);

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
                          this,
                          getDataMessage.Inventories[0].Hash.ToHexString(),
                          inventory.Hash.ToHexString())
                          .Log(LogFile);
                      }
                      else
                      {
                        // Send notfound
                      }
                    }
                    else if(inventory.Type == InventoryType.MSG_BLOCK)
                    {
                      if (Blockchain.TryReadHeader(
                        inventory.Hash,
                        out Header header))
                      {
                        byte[] blockArchive;

                        if(IndexBlockArchiveCache == header.IndexBlockArchive)
                        {
                          blockArchive = CacheBlockArchive;
                        }
                        else
                        {
                          blockArchive = Blockchain.Archiver.LoadBlockArchive(
                           header.IndexBlockArchive);

                          IndexBlockArchiveCache = header.IndexBlockArchive;
                          CacheBlockArchive = blockArchive;
                        }

                        await SendMessage(new BlockMessage(
                          blockArchive,
                          header.StartIndexBlockArchive,
                          header.CountBlockBytes));
                      }
                      else
                      {
                        await SendMessage(new RejectMessage(inventory.Hash));
                      }
                    }
                  }

                  break;

                default:
                  break;
              }
            }
          }
        }
        catch (Exception ex)
        {
          SetFlagDisposed(
            $"{ex.GetType().Name} in listener: \n{ex.Message}");

          if (IsStateAwaitingHeader())
            Blockchain.ReleaseLock();
          else if(IsStateAwaitingBlockDownload())
            Network.ReturnPeerBlockDownloadIncomplete(this);
        }
      }

      public void ProcessHeaderUnsolicited(Header header)
      {
        if (Blockchain.TryReadHeader(
          header.Hash,
          out Header headerReceivedNow))
        {
          if (HeaderDuplicateReceivedLast != null &&
            HeaderDuplicateReceivedLast.Height >= headerReceivedNow.Height)
          {
            throw new ProtocolException($"Sent duplicate header {header}.");
          }

          HeaderDuplicateReceivedLast = headerReceivedNow;
        }
        else
        {
          if (!FlagSynchronizationScheduled)
          {
            CountOrphanReceived = 0;
            FlagSynchronizationScheduled = true;
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
                            
      public async Task GetHeaders()
      {
        HeaderDownload = new(Blockchain.GetLocator(), this);
               
        ($"Send getheaders to peer {this},\n" +
          $"locator: {HeaderDownload.ToStringLocator()}")
          .Log(LogFile);

        SetStateAwaitingHeader();

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        await SendMessage(new GetHeadersMessage(
          HeaderDownload.Locator,
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

        Cancellation.CancelAfter(TIMEOUT_RESPONSE_MILLISECONDS);

        BlockDownload.StopwatchBlockDownload.Restart();

        try
        {
          await SendMessage(new GetDataMessage(inventories));
        }
        catch(Exception ex)
        {
          SetFlagDisposed(
            $"{ex.GetType().Name} when sending getBlock message: {ex.Message}");

          Network.ReturnPeerBlockDownloadIncomplete(this);
          return;
        }

        lock (this)
        {
          State = StateProtocol.AwaitingBlockDownload;
        }

        CountStartBlockDownload++;
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
            !FlagSynchronizationScheduled ||
            FlagDispose ||
            IsBusy)
          {
            return false;
          }

          FlagSynchronizationScheduled = false;
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

      public override string ToString()
      {
        return IPAddress.ToString();
      }

      public void SetFlagDisposed(string message)
      {
        $"Set flag dispose on peer {this}: {message}"
          .Log(LogFile);

        FlagDispose = true;
      }

      public void Dispose()
      {
        TcpClient.Dispose();

        LogFile.Dispose();

        File.Move(
          Path.Combine(DirectoryLogPeers.FullName, ToString()),
          Path.Combine(DirectoryLogPeersDisposed.FullName, ToString()));
      }

      public string GetStatus()
      {
        lock(this)
        {
          long lifeTime =
           DateTimeOffset.UtcNow.ToUnixTimeSeconds() - TimeCreation;

          return
            $"\n Status peer {this}:\n" +
            $"lifeTime: {lifeTime}\n" +
            $"IsBusy: {IsBusy}\n" +
            $"State: {State}\n" +
            $"FlagDispose: {FlagDispose}\n" +
            $"Connection: {Connection}\n" +
            $"CountStartBlockDownload: {CountStartBlockDownload}\n" +
            $"CountInsertBlockDownload: {CountInsertBlockDownload}\n" +
            $"CountWastedBlockDownload: {CountWastedBlockDownload}\n" +
            $"CountBlockingBlockDownload: {CountWastedBlockDownload}\n" +
            $"FlagSynchronizationScheduled: {FlagSynchronizationScheduled}\n";
        }
      }
    }
  }
}
