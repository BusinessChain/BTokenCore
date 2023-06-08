using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public Token TokenParent;
    public Token TokenChild;

    Header HeaderGenesis;
    public Header HeaderTip;

    Dictionary<int, List<Header>> HeaderIndex = new();

    BlockArchiver Archiver;

    public Wallet Wallet;

    protected TXPool TXPool;

    public Network Network;
    public UInt16 Port;

    protected StreamWriter LogFile;



    static string NameFork = "Fork";
    static string NameImage = "Image";
    static string NameImageOld = "ImageOld";

    string PathImage;
    string PathImageOld;
    string PathImageFork;
    string PathImageForkOld;

    string PathRootToken;

    const int INTERVAL_BLOCKHEIGHT_IMAGE = 5;

    protected int CountBytesDataTokenBasis = 120;

    bool IsLocked;
    static object LOCK_Token = new();


    public Token(
      UInt16 port, 
      bool flagEnableInboundConnections)
    {
      PathRootToken = GetName();
      Directory.CreateDirectory(PathRootToken);

      HeaderGenesis = CreateHeaderGenesis();

      HeaderTip = HeaderGenesis;

      HeaderIndex.Clear();
      IndexingHeaderTip();

      Archiver = new(GetName());

      TXPool = new();

      Port = port;
      Network = new(this, flagEnableInboundConnections);

      Wallet = new(File.ReadAllText($"Wallet{GetName()}/wallet"));

      PathImage = Path.Combine(
        PathRootToken,
        NameImage);

      PathImageOld = Path.Combine(
        PathRootToken,
        NameImageOld);

      PathImageFork = Path.Combine(
        PathRootToken,
        NameFork,
        NameImage);

      PathImageForkOld = Path.Combine(
        PathRootToken,
        NameFork,
        NameImageOld);

      LogFile = new StreamWriter(
        Path.Combine(GetName(), "LogToken"),
        false);
    }

    public void Start(bool recursive = false)
    {
      if (recursive && TokenParent != null)
        TokenParent.Start(recursive: true);

      LoadImage();

      Network.Start();
    }

    public void PrintChain(ref string text)
    {
      if (TokenParent != null)
        TokenParent.PrintChain(ref text);

      text += $"\nPrint chain {GetName()}.\n";


      Block block = CreateBlock();
      int i = 1;

      while (Archiver.TryLoadBlockArchive(i, out byte[] buffer))
      {
        block.Buffer = buffer;
        block.Parse();

        text += $"{i} -> {block.Header}\n";

        if(TokenChild != null)
        {
          List<TX> tXs = block.TXs;

          foreach (TX tX in tXs)
            foreach (TXOutput tXOutput in tX.TXOutputs)
              if (tXOutput.Value == 0)
              {
                text += $"\t{tX}\t";

                int index = tXOutput.StartIndexScript + 4;

                text += $"\t{tXOutput.Buffer.Skip(index).Take(32).ToArray().ToHexString()}\n";
              }
        }

        i++;
      }
    }

    public string GetStatus()
    {
      string messageStatus = "";

      if (TokenParent != null)
        messageStatus += TokenParent.GetStatus();

      var ageBlock = TimeSpan.FromSeconds(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - HeaderTip.UnixTimeSeconds);

      messageStatus +=
        $"\n\t\t\t\t{GetName()}:\n" +
        $"Height: {HeaderTip.Height}\n" +
        $"Block tip: {HeaderTip}\n" +
        $"Difficulty Tip: {HeaderTip.Difficulty}\n" +
        $"Acc. Difficulty: {HeaderTip.DifficultyAccumulated}\n" +
        $"Timestamp: {DateTimeOffset.FromUnixTimeSeconds(HeaderTip.UnixTimeSeconds)}\n" +
        $"Age: {ageBlock}\n" +
        $"\n{Wallet.GetStatus()}";

      return messageStatus;
    }

    public abstract List<string> GetSeedAddresses();

    public bool TryLock()
    {
      if (TokenParent != null)
        return TokenParent.TryLock();

      lock (LOCK_Token)
      {
        if (IsLocked)
          return false;

        IsLocked = true;
        return true;
      }
    }

    public void ReleaseLock()
    {
      if (TokenParent == null)
        IsLocked = false;
      else
        TokenParent.ReleaseLock();
    }

    public abstract Header CreateHeaderGenesis();

    internal void Reorganize()
    {
      $"Reorganize token {this.GetType().Name}".Log(LogFile);

      PathImageFork.TryMoveDirectoryTo(PathImage);
      PathImageForkOld.TryMoveDirectoryTo(PathImageOld);
    }

    public void LoadImage(int heightMax = int.MaxValue)
    {
      $"Load image{(heightMax < int.MaxValue ? $" with maximal height {heightMax}" : "")}.".Log(LogFile);

      string pathImageLoad = PathImage;

      while (true)
      {
        Reset();

        try
        {
          $"Load image headerchain of token {GetName()}".Log(LogFile);
          LoadImageHeaderchain(pathImageLoad, heightMax);

          LoadImageDatabase(pathImageLoad);
          Wallet.LoadImage(pathImageLoad);
        }
        catch
        {
          Reset();

          if (pathImageLoad == PathImage)
          {
            pathImageLoad = PathImageOld;
            continue;
          }
        }

        Block block = CreateBlock();
        int heightBlock = HeaderTip.Height + 1;

        while (
          heightBlock <= heightMax &&
          Archiver.TryLoadBlockArchive(heightBlock, out byte[] buffer))
        {
          block.Buffer = buffer;
          block.Parse();

          try
          {
            block.Header.AppendToHeader(HeaderTip);
            InsertInDatabase(block);
            AppendHeader(block.Header);

            if (TokenChild != null)
              TokenChild.SignalCompletionBlockInsertion(block.Header);
          }
          catch
          {
            break;
          }

          heightBlock += 1;
        }

        return;
      }
    }

    public virtual void LoadImageHeaderchain(
      string pathImage,
      int heightMax)
    {
      byte[] bytesHeaderImage = File.ReadAllBytes(
        Path.Combine(pathImage, "ImageHeaderchain"));

      int index = 0;

      while (index < bytesHeaderImage.Length)
      {
        Header header = ParseHeader(
         bytesHeaderImage,
         ref index);

        // IndexBlockArchive und StartIndexBlockArchive braucht es eigentlich nicht.

        header.IndexBlockArchive = BitConverter.ToInt32(
          bytesHeaderImage, index);

        index += 4;

        header.StartIndexBlockArchive = BitConverter.ToInt32(
          bytesHeaderImage, index);

        index += 4;

        header.CountBytesBlock = BitConverter.ToInt32(
          bytesHeaderImage, index);

        index += 4;

        $"Append {header} to headerTip {HeaderTip}".Log(LogFile);

        header.AppendToHeader(HeaderTip);

        HeaderTip.HeaderNext = header;
        HeaderTip = header;

        IndexingHeaderTip();
      }

      if (HeaderTip.Height > heightMax)
        throw new ProtocolException(
          $"Image higher than desired height {heightMax}.");
    }

    public virtual void LoadImageDatabase(string path)
    { }

    public bool IsFork;

    public void CreateImage()
    {
      string pathImage;

      if (IsFork)
      {
        pathImage = PathImageFork;
        pathImage.TryMoveDirectoryTo(PathImageForkOld);
      }
      else
      {
        pathImage = PathImage;
        pathImage.TryMoveDirectoryTo(PathImageOld);
      }

      Directory.CreateDirectory(pathImage);

      CreateImageHeaderchain(pathImage);

      CreateImageDatabase(pathImage);
      Wallet.CreateImage(pathImage);

      if (TokenChild != null)
        TokenChild.CreateImage();
    }

    public virtual void CreateImageHeaderchain(string path)
    {
      using (FileStream fileImageHeaderchain = new(
          Path.Combine(path, "ImageHeaderchain"),
          FileMode.Create,
          FileAccess.Write,
          FileShare.None))
      {
        Header header = HeaderGenesis.HeaderNext;

        while (header != null)
        {
          byte[] headerBytes = header.GetBytes();

          fileImageHeaderchain.Write(
            headerBytes, 0, headerBytes.Length);

          byte[] bytesIndexBlockArchive =
            BitConverter.GetBytes(header.IndexBlockArchive);

          fileImageHeaderchain.Write(
            bytesIndexBlockArchive, 0, bytesIndexBlockArchive.Length);

          byte[] bytesStartIndexBlockArchive =
            BitConverter.GetBytes(header.StartIndexBlockArchive);

          fileImageHeaderchain.Write(
            bytesStartIndexBlockArchive, 0, bytesStartIndexBlockArchive.Length);

          byte[] bytesCountBlockBytes =
            BitConverter.GetBytes(header.CountBytesBlock);

          fileImageHeaderchain.Write(
            bytesCountBlockBytes, 0, bytesCountBlockBytes.Length);

          header = header.HeaderNext;
        }
      }
    }

    public virtual void CreateImageDatabase(string path)
    { }

    public virtual void Reset()
    {
      HeaderGenesis = CreateHeaderGenesis();

      HeaderTip = HeaderGenesis;

      HeaderIndex.Clear();
      IndexingHeaderTip();
      Wallet.Clear();
    }

    public abstract Block CreateBlock();

    protected TX CreateCoinbaseTX(Block block, int height, long blockReward)
    {
      List<byte> tXRaw = new();

      tXRaw.AddRange(new byte[4] { 0x01, 0x00, 0x00, 0x00 }); // version

      tXRaw.Add(0x01); // #TxIn

      tXRaw.AddRange(new byte[32]); // TxOutHash

      tXRaw.AddRange("FFFFFFFF".ToBinary()); // TxOutIndex

      List<byte> blockHeight = VarInt.GetBytes(height); // Script coinbase
      tXRaw.Add((byte)blockHeight.Count);
      tXRaw.AddRange(blockHeight);

      tXRaw.AddRange("FFFFFFFF".ToBinary()); // sequence

      tXRaw.Add(0x01); // #TxOut

      tXRaw.AddRange(BitConverter.GetBytes(blockReward));

      tXRaw.AddRange(Wallet.GetReceptionScript());

      tXRaw.AddRange(new byte[4]);

      int indexTXRaw = 0;

      TX tX = block.ParseTX(
        true,
        tXRaw.ToArray(),
        ref indexTXRaw);

      tX.TXRaw = tXRaw;

      return tX;
    }

    protected bool IsMining;

    public void StopMining()
    {
        IsMining = false;
    }

    public abstract void StartMining();

    const int ORDER_AVERAGEING_FEEPERBYTE = 3;
    public double FeePerByteAverage;

    public void InsertBlock(Block block)
    {
      block.Header.AppendToHeader(HeaderTip);

      InsertInDatabase(block);

      TXPool.RemoveTXs(block.TXs.Select(tX => tX.Hash));

      AppendHeader(block.Header);

      FeePerByteAverage =
        ((ORDER_AVERAGEING_FEEPERBYTE - 1) * FeePerByteAverage + block.FeePerByte) /
        ORDER_AVERAGEING_FEEPERBYTE;

      if (TokenChild != null)
        TokenChild.SignalCompletionBlockInsertion(block.Header);

      Archiver.ArchiveBlock(block);

      if (block.Header.Height % INTERVAL_BLOCKHEIGHT_IMAGE == 0 && 
        TokenParent == null)
        CreateImage();
    }

    public virtual Block GetBlock(byte[] hash)
    {
      if(TryGetHeader(hash, out Header header))
        if (Archiver.TryLoadBlockArchive(header.Height, out byte[] buffer))
        {
          Block block = CreateBlock();

          block.Buffer = buffer;
          block.Parse();

          return block;
        }

      return null;
    }

    public virtual void InsertDB(
      byte[] bufferDB,
      int lengthDataInBuffer)
    { throw new NotImplementedException(); }

    public virtual void DeleteDB()
    { throw new NotImplementedException(); }

    public virtual void DetectAnchorTokenInBlock(TX tX)
    { throw new NotImplementedException(); }

    public virtual void SignalCompletionBlockInsertion(Header header)
    { throw new NotImplementedException(); }

    public virtual void RevokeBlockInsertion()
    { throw new NotImplementedException(); }

    public virtual List<byte[]> ParseHashesDB(
      byte[] buffer,
      int length,
      Header headerTip)
    { throw new NotImplementedException(); }

    protected abstract void InsertInDatabase(Block block);

    public abstract Header ParseHeader(
      byte[] buffer,
      ref int index);

    public string GetName()
    {
      return GetType().Name;
    }

    public virtual bool TryGetDB(
      byte[] hash,
      out byte[] dataDB)
    { throw new NotImplementedException(); }

    public virtual bool FlagDownloadDBWhenSync(HeaderDownload header)
    { return false; }

    public void BroadcastTX(TX tX)
    {
      TXPool.AddTX(tX);
      //Network.AdvertizeTX(tX);
    }

    public List<Header> GetLocator()
    {
      Header header = HeaderTip;
      List<Header> locator = new();
      int depth = 0;
      int nextLocationDepth = 0;

      while (header != null)
      {
        if (depth == nextLocationDepth || header.HeaderPrevious == null)
        {
          locator.Add(header);
          nextLocationDepth = 2 * nextLocationDepth + 1;
        }

        depth++;

        header = header.HeaderPrevious;
      }

      return locator;
    }

    public List<Header> GetHeaders(
      IEnumerable<byte[]> locatorHashes,
      int count,
      byte[] stopHash)
    {
      foreach (byte[] hash in locatorHashes)
      {
        if (TryGetHeader(hash, out Header header))
        {
          List<Header> headers = new();

          while (
            header.HeaderNext != null &&
            headers.Count < count &&
            !header.Hash.IsEqual(stopHash))
          {
            Header nextHeader = header.HeaderNext;

            headers.Add(nextHeader);
            header = nextHeader;
          }

          return headers;
        }
      }

      throw new ProtocolException(string.Format(
        "Locator does not root in headerchain."));
    }

    public void AppendHeader(Header header)
    {
      HeaderTip.HeaderNext = header;
      HeaderTip = header;

      IndexingHeaderTip();
    }


    public bool TryGetHeader(
      byte[] headerHash,
      out Header header)
    {
      int key = BitConverter.ToInt32(headerHash, 0);

      lock (HeaderIndex)
        if (HeaderIndex.TryGetValue(
          key,
          out List<Header> headers))
        {
          foreach (Header h in headers)
            if (headerHash.IsEqual(h.Hash))
            {
              header = h;
              return true;
            }
        }

      header = null;
      return false;
    }

    public void IndexingHeaderTip()
    {
      int keyHeader = BitConverter.ToInt32(HeaderTip.Hash, 0);

      lock (HeaderIndex)
      {
        if (!HeaderIndex.TryGetValue(keyHeader, out List<Header> headers))
        {
          headers = new List<Header>();
          HeaderIndex.Add(keyHeader, headers);
        }

        headers.Add(HeaderTip);
      }
    }
  }
}
