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

    public Header HeaderGenesis;
    public Header HeaderTip;

    Dictionary<int, List<Header>> HeaderIndex = new();

    protected BlockArchiver Archiver;

    public Wallet Wallet;

    public TXPool TXPool;

    public Network Network;
    public UInt16 Port;

    protected StreamWriter LogFile;

    static string NameImage = "Image";
    static string NameImageOld = "ImageOld";

    string PathImage;
    string PathImageOld;

    string PathRootToken;

    const int INTERVAL_BLOCKHEIGHT_IMAGE = 5;

    protected int CountBytesDataTokenBasis = 120;

    const int ORDER_AVERAGEING_FEEPERBYTE = 3;
    public double FeePerByteAverage;

    bool IsLocked;
    static object LOCK_Token = new();


    public Token(
      UInt16 port, 
      bool flagEnableInboundConnections)
    {
      PathRootToken = GetName();
      Directory.CreateDirectory(PathRootToken);

      HeaderGenesis = CreateHeaderGenesis();

      Archiver = new(GetName());

      TXPool = new();

      Port = port;
      Network = new(this, flagEnableInboundConnections);

      Wallet = new(File.ReadAllText($"Wallet{GetName()}/wallet"));

      PathImage = Path.Combine(PathRootToken, NameImage);
      PathImageOld = Path.Combine(PathRootToken, NameImageOld);

      LogFile = new StreamWriter(
        Path.Combine(GetName(), "LogToken"),
        false);

      Reset();
    }

    public void Start()
    {
      Token token = this;

      while (token.TokenParent != null)
        token = TokenParent;

      token.LoadImage();

      while (token != null)
      {
        token.Network.Start();
        token = token.TokenChild;
      }
    }

    public void PrintImage(ref string text)
    {
      try
      {
        if (TokenParent != null)
          TokenParent.PrintImage(ref text);

        text += $"\nPrint Image {GetName()}.\n";

        string pathHeaderchain = Path.Combine(GetName(), NameImage, "ImageHeaderchain");

        byte[] bytesHeaderImage = File.ReadAllBytes(pathHeaderchain);

        text += $"Loaded image headerchain {pathHeaderchain}.\n";

        int index = 0;
        int heightHeader = 0;

        while (index < bytesHeaderImage.Length)
        {
          Block block = CreateBlock();

          Header header = block.ParseHeader(
            bytesHeaderImage,
            ref index);

          heightHeader += 1;

          header.CountBytesBlock = BitConverter.ToInt32(
            bytesHeaderImage, index);

          index += 4;

          text += $"{heightHeader}, {header}";

          byte flagHasHashChild = bytesHeaderImage[index];
          index += 1;

          if (flagHasHashChild == 0x01)
          {
            header.HashChild = new byte[32];
            Array.Copy(bytesHeaderImage, index, header.HashChild, 0, 32);
            index += 32;

            text += $" -> {header.HashChild.ToHexString()}";
          }

          text += "\n";
        }
      }
      catch(Exception ex)
      {
        Console.WriteLine($"Exception {ex.GetType().Name} when printing image.");
      }
    }

    public void PrintBlocks(ref string text)
    {
      if (TokenParent != null)
        TokenParent.PrintBlocks(ref text);

      text += $"\nPrint blocks {GetName()}.\n";


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
        $"Age: {ageBlock}\n";// +
        //$"\n{Wallet.GetStatus()}";

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

    public void ForkChain(int heightFork)
    {
      LoadImage(heightFork);
      Archiver.SetBlockPathToFork();
    }

    public void Reorganize()
    {
      Archiver.Reorganize();
    }

    public void LoadImage(int heightMax = int.MaxValue)
    {
      Reset();

      string pathImage = Path.Combine(GetName(), NameImage);

      while (true)
      {
        try
        {
          ($"Load image of token {pathImage}" +
            $"{(heightMax < int.MaxValue ? $" with maximal height {heightMax}" : "")}.").Log(LogFile);

          LoadImageHeaderchain(pathImage);
          LoadImageDatabase(pathImage);

          if (HeaderTip.Height > heightMax)
            throw new ProtocolException(
              $"Image height of {GetName()} higher than desired height {heightMax}.");

          break;
        }
        catch
        {
          Reset();

          if (Directory.Exists(pathImage))
            Directory.Delete(pathImage, recursive: true);

          try
          {
            Directory.Move(
              Path.Combine(GetName(), NameImageOld), 
              pathImage);
          }
          catch(DirectoryNotFoundException)
          {
            break;
          }
        }
      }

      Block block = CreateBlock();
      int heightBlock = HeaderTip.Height + 1;

      while (
        heightBlock <= heightMax &&
        Archiver.TryLoadBlockArchive(heightBlock, out byte[] buffer))
      {
        $"Pull block height {heightBlock} from Archiver of {GetName()}.".Log(LogFile);

        block.Buffer = buffer;
        block.Parse();

        try
        {
          InsertBlock(block);
        }
        catch
        {
          Archiver.CleanAfterBlockHeight(HeaderTip.Height);
          break;
        }

        heightBlock += 1;
      }

      if (TokenChild != null)
        TokenChild.LoadImage(HeaderTip.Height);
    }

    public virtual void Reset()
    {
      Archiver.ResetBlockPath();

      HeaderTip = HeaderGenesis;
      HeaderIndex.Clear();
      IndexingHeaderTip();

      Wallet.Clear();

      if (TokenChild != null)
        TokenChild.Reset();
    }

    public void LoadImageHeaderchain(string pathImage)
    {
      byte[] bytesHeaderImage = File.ReadAllBytes(
        Path.Combine(pathImage, "ImageHeaderchain"));

      int index = 0;

      $"Load headerchain of {GetName()}.".Log(LogFile);

      while (index < bytesHeaderImage.Length)
      {
        Block block = CreateBlock();

        Header header = block.ParseHeader(
          bytesHeaderImage,
          ref index);

        header.CountBytesBlock = BitConverter.ToInt32(
          bytesHeaderImage, index);

        index += 4;

        byte flagHasHashChild = bytesHeaderImage[index];
        index += 1;

        if(flagHasHashChild == 0x01)
        {
          header.HashChild = new byte[32];
          Array.Copy(bytesHeaderImage, index, header.HashChild, 0, 32);
          index += 32;
        }

        $"Append {header} to headerTip {HeaderTip}.".Log(LogFile);

        header.AppendToHeader(HeaderTip);

        HeaderTip.HeaderNext = header;
        HeaderTip = header;

        IndexingHeaderTip();
      }
    }

    public virtual void LoadImageDatabase(string path)
    {
      Wallet.LoadImage(path);
    }

    public void InsertBlock(Block block)
    {
      block.Header.AppendToHeader(HeaderTip);
      InsertInDatabase(block);
      AppendHeaderToTip(block.Header);

      FeePerByteAverage =
        ((ORDER_AVERAGEING_FEEPERBYTE - 1) * FeePerByteAverage + block.FeePerByte) /
        ORDER_AVERAGEING_FEEPERBYTE;

      TXPool.RemoveTXs(block.TXs.Select(tX => tX.Hash));
            
      Archiver.ArchiveBlock(block);

      if (block.Header.Height % INTERVAL_BLOCKHEIGHT_IMAGE == 0)
        CreateImage();

      if (TokenChild != null)
      {
        TokenChild.SignalParentBlockInsertion(
          block.Header,
          out block.BlockChild);
      }
    }

    public void CreateImage()
    {
      PathImage.TryMoveDirectoryTo(PathImageOld);

      Directory.CreateDirectory(PathImage);

      CreateImageHeaderchain(PathImage);

      CreateImageDatabase(PathImage);

      Wallet.CreateImage(PathImage);
    }

    void CreateImageHeaderchain(string pathImage)
    {
      using (FileStream fileImageHeaderchain = new(
          Path.Combine(pathImage, "ImageHeaderchain"),
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

          fileImageHeaderchain.Write(
            BitConverter.GetBytes(header.CountBytesBlock), 0, 4);

          if(header.HashChild == null)
            fileImageHeaderchain.WriteByte(0x00);
          else
          {
            fileImageHeaderchain.WriteByte(0x01);
            fileImageHeaderchain.Write(header.HashChild, 0, 32);
          }

          header = header.HeaderNext;
        }
      }
    }

    public virtual void CreateImageDatabase(string path)
    { }

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

      TX tX = Block.ParseTX(
        true,
        tXRaw.ToArray(),
        ref indexTXRaw,
        block.SHA256);

      tX.TXRaw = tXRaw;

      return tX;
    }

    protected bool IsMining;

    public void StopMining()
    {
        IsMining = false;
    }

    public abstract void StartMining();
    
    public bool TryGetBlockBytes(byte[] hash, out byte[] buffer)
    {
      buffer = null;

      if (TryGetHeader(hash, out Header header))
        if (Archiver.TryLoadBlockArchive(header.Height, out buffer))
          return true;

      return false;
    }

    public virtual void InsertDB(
      byte[] bufferDB,
      int lengthDataInBuffer)
    { throw new NotImplementedException(); }

    public virtual void DeleteDB()
    { throw new NotImplementedException(); }

    public virtual void DetectAnchorTokenInBlock(TX tX)
    { throw new NotImplementedException(); }

    public virtual void SignalParentBlockInsertion(
      Header header, out Block block)
    { throw new NotImplementedException(); }

    public virtual void RevokeBlockInsertion()
    { throw new NotImplementedException(); }

    public virtual List<byte[]> ParseHashesDB(
      byte[] buffer,
      int length,
      Header headerTip)
    { throw new NotImplementedException(); }

    protected abstract void InsertInDatabase(Block block);

    public string GetName()
    {
      return GetType().Name;
    }

    public virtual bool TryGetDB(
      byte[] hash,
      out byte[] dataDB)
    { throw new NotImplementedException(); }

    public void BroadcastTX(TX tX)
    {
      TXPool.TryAddTX(tX);
      Network.AdvertizeTX(tX);
    }

    public void BroadcastTX(List<TX> tXs)
    {
      tXs.ForEach(tX => TXPool.TryAddTX(tX));
      Network.AdvertizeTXs(tXs);
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

    public void AppendHeaderToTip(Header header)
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
