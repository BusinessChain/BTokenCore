using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Collections.Concurrent;



namespace BTokenLib
{
  public partial class Blockchain
  {
    public Token Token;

    Header HeaderGenesis;
    public Header HeaderTip;

    Dictionary<int, List<Header>> HeaderIndex = new();


    // Brauche ich das?
    Dictionary<byte[], int> BlockIndex = new(
      new EqualityComparerByteArray());

    string NameFork = "Fork";
    string NameImage = "Image";
    string NameImageOld = "ImageOld";

    string FileNameIndexBlockArchiveImage = "IndexBlockArchive";
    string PathBlockArchive;

    object LOCK_IsBlockchainLocked = new();
    bool IsBlockchainLocked;
        
    public const int COUNT_LOADER_TASKS = 6;
    int SIZE_BLOCK_ARCHIVE_BYTES = 0x1000000;
    const int UTXOIMAGE_INTERVAL_LOADER = 200;

    StreamWriter LogFile;

    object LOCK_IndexBlockArchiveLoad = new();
    int IndexBlockArchiveLoad;
    object LOCK_IndexBlockLoadInsert = new();
    int IndexBlockArchiveInsert;



    public Blockchain(
      Token token,
      string pathBlockArchive)
    {
      Token = token;

      PathBlockArchive = pathBlockArchive;

      DirectoryInfo DirectoryBlockArchive =
          Directory.CreateDirectory(PathBlockArchive);

      LogFile = new StreamWriter(
        Path.Combine(Token.GetName() + "LogArchiver"), 
        false);

      InizializeHeaderchain();
    }

    public string GetStatus()
    {
      var ageBlock = TimeSpan.FromSeconds(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - HeaderTip.UnixTimeSeconds);

      return 
        "\n Status Blockchain:\n" +
        $"Height: {HeaderTip.Height}\n" +
        $"Block tip: {HeaderTip}\n" +
        $"Timestamp: {DateTimeOffset.FromUnixTimeSeconds(HeaderTip.UnixTimeSeconds)}\n" +
        $"Age: {ageBlock}\n";
    }


    public void LoadImage()
    {
      LoadImage(0, new byte[32]);
    }
        
    void LoadImage(int heightMax, byte[] hashStopLoading)
    {
      string pathImage = NameImage;

      while (true)
      {
        if (!TryLoadImageFile(pathImage, heightMax))
        {
          if (pathImage == NameImage)
          {
            pathImage = NameImageOld;
            continue;
          }
          else
          {
            pathImage = NameImage;
          }
        }

        HashStopLoading = hashStopLoading;
        IndexBlockArchiveInsert = IndexBlockArchiveLoad;
        IsLoaderFail = false;
        FlagLoaderExit = false;

        if (FileBlockArchive != null)
          FileBlockArchive.Dispose();

        Parallel.For(
          0,
          COUNT_LOADER_TASKS,
          i => StartLoader());

        if (!IsLoaderFail)
          return;
      }
    }

    bool TryLoadImageFile(string pathImage, int heightMax)
    {
      Reset();

      try
      {
        LoadImageHeaderchain(pathImage);

        if(heightMax > 0 && HeaderTip.Height > heightMax)
        {
          return false;
        }

        Token.LoadImage(pathImage);

        LoadMapBlockToArchiveData(
          File.ReadAllBytes(
            Path.Combine(pathImage, "MapBlockHeader")));

        IndexBlockArchiveLoad = BitConverter.ToInt32(
          File.ReadAllBytes(
            Path.Combine(
              pathImage,
              FileNameIndexBlockArchiveImage)),
          0);

        return true;
      }
      catch(Exception ex)
      {
        Console.WriteLine(
          $"{ex.GetType().Name} when loading image {pathImage}:" +
          $"\n{ex.Message}");

        Reset();

        return false;
      }
    }

    void LoadImageHeaderchain(string pathImage)
    {
      byte[] bytesHeaderImage = File.ReadAllBytes(
        Path.Combine(pathImage, "ImageHeaderchain"));

      int indexBytesHeaderImage = 0;

      while (indexBytesHeaderImage < bytesHeaderImage.Length)
      {
        Header header = Token.ParseHeader(
         bytesHeaderImage,
         ref indexBytesHeaderImage,
         SHA256.Create());

        if (!header.HashPrevious.IsEqual(HeaderTip.Hash))
        {
          throw new ProtocolException("Header image corrupted.");
        }

        InsertHeader(header);
      }
    }

    void LoadMapBlockToArchiveData(byte[] buffer)
    {
      int index = 0;

      while (index < buffer.Length)
      {
        byte[] key = new byte[32];
        Array.Copy(buffer, index, key, 0, 32);
        index += 32;

        int value = BitConverter.ToInt32(buffer, index);
        index += 4;

        BlockIndex.Add(key, value);
      }
    }

    void Reset()
    {
      InizializeHeaderchain();

      Token.Reset();

      BlockIndex.Clear();

      IndexBlockArchiveLoad = 1;
    }

    void InizializeHeaderchain()
    {
      HeaderIndex.Clear();

      HeaderGenesis = Token.CreateHeaderGenesis();
      HeaderGenesis.Height = 0;
      HeaderGenesis.DifficultyAccumulated = HeaderGenesis.Difficulty;

      HeaderTip = HeaderGenesis;

      UpdateHeaderIndex(HeaderTip);
    }


    byte[] HashStopLoading;
    Dictionary<int, Thread> ThreadsSleeping = new();
    Dictionary<int, BlockLoad> QueueBlockLoads = new();
    bool IsLoaderFail;
    bool FlagLoaderExit;
    int CountBytesArchive;
    FileStream FileBlockArchive;
    ConcurrentBag<BlockLoad> PoolBlockLoad = new();

    void StartLoader()
    {
      BlockLoad blockLoad = new(Token);

      while (true)
      {
        lock (LOCK_IndexBlockArchiveLoad)
        {
          blockLoad.Initialize(IndexBlockArchiveLoad++);
        }

        string pathBlockArchive = Path.Combine(
          PathBlockArchive,
          blockLoad.Index.ToString());

        try
        {
          blockLoad.Parse(
            File.ReadAllBytes(pathBlockArchive));
        }
        catch (FileNotFoundException)
        {
          blockLoad.IsInvalid = true;
        }
        catch (ProtocolException ex)
        {
          blockLoad.IsInvalid = true;

          ($"ProtocolException when loading file {pathBlockArchive}:\n " +
            $"{ex.Message}")
            .Log(LogFile);
        }

        bool flagPutThreadToSleep = false;

      LABEL_PutThreadToSleep:

        if (flagPutThreadToSleep)
        {
          try
          {
            Thread.Sleep(Timeout.Infinite);
          }
          catch (ThreadInterruptedException)
          {
            if (FlagLoaderExit)
              return;

            flagPutThreadToSleep = false;
          }
        }

        lock (LOCK_IndexBlockLoadInsert)
        {
          if (blockLoad.Index != IndexBlockArchiveInsert)
          {
            if (
              QueueBlockLoads.Count < COUNT_LOADER_TASKS ||
              QueueBlockLoads.Keys.Any(k => k > blockLoad.Index))
            {
              QueueBlockLoads.Add(blockLoad.Index, blockLoad);

              if(!PoolBlockLoad.TryTake(out blockLoad))
                blockLoad = new(Token);

              continue;
            }

            if (FlagLoaderExit)
              return;

            ThreadsSleeping.Add(
              blockLoad.Index,
              Thread.CurrentThread);

            flagPutThreadToSleep = true;
            goto LABEL_PutThreadToSleep;
          }
        }

        if (
          blockLoad.IsInvalid ||
          blockLoad.Blocks.Count == 0 ||
          !blockLoad.Blocks[0].Header.HashPrevious.IsEqual(HeaderTip.Hash) ||
          !TryBlockLoadInsert(blockLoad))
        {
          CreateBlockArchive(blockLoad.Index);
          break;
        }

        if (blockLoad.CountBytes < SIZE_BLOCK_ARCHIVE_BYTES)
        {
          CountBytesArchive = blockLoad.CountBytes;

          OpenBlockArchive(blockLoad.Index);
          break;
        }

        if (blockLoad.Index % UTXOIMAGE_INTERVAL_LOADER == 0)
          CreateImage(++blockLoad.Index, NameImage);

        lock (LOCK_IndexBlockLoadInsert)
        {
          IndexBlockArchiveInsert += 1;

          if(QueueBlockLoads.ContainsKey(IndexBlockArchiveInsert))
          {
            PoolBlockLoad.Add(blockLoad);

            blockLoad = QueueBlockLoads[IndexBlockArchiveInsert];
            QueueBlockLoads.Remove(IndexBlockArchiveInsert);

            goto LABEL_PutThreadToSleep;
          }

          if (ThreadsSleeping.TryGetValue(
            IndexBlockArchiveInsert,
            out Thread threadSleeping))
          {
            ThreadsSleeping.Remove(IndexBlockArchiveInsert);
            threadSleeping.Interrupt();
          }
        }
      }

      lock(LOCK_IndexBlockLoadInsert)
      {
        FlagLoaderExit = true;

        foreach (Thread threadSleeping in ThreadsSleeping.Values)
          threadSleeping.Interrupt();
      }

      ThreadsSleeping.Clear();
      PoolBlockLoad.Clear();
    }


    bool TryBlockLoadInsert(BlockLoad blockLoad)
    {
      try
      {
        foreach (Block block in blockLoad.Blocks)
        {
          InsertHeader(block.Header);

          Token.InsertBlock(
            block,
            blockLoad.Index);

          if (block.Header.Hash.IsEqual(HashStopLoading))
          {
            FileBlockArchive.Dispose();

            CreateBlockArchive(blockLoad.Index);

            foreach (Block blockArchiveFork in blockLoad.Blocks)
            {
              ArchiveBlock(blockArchiveFork, -1);

              if (blockArchiveFork == block)
                break;
            }
          }
        }

        Debug.WriteLine(
          $"Loaded blockchain height: {HeaderTip.Height}, " +
          $"blockload Index: {blockLoad.Index}");
      }
      catch (ProtocolException)
      {
        FileBlockArchive.Dispose();

        File.Delete(FileBlockArchive.Name);

        IsLoaderFail = true;
      }

      return !IsLoaderFail;
    }

    public void InsertBlock(
      Block block,
      int intervalArchiveImage = 3)
    {
      InsertHeader(block.Header);

      Token.InsertBlock(
        block,
        IndexBlockArchiveInsert);

      ArchiveBlock(
        block,
        intervalArchiveImage);
    }

    public void InsertHeader(Header header)
    {
      header.ExtendHeaderTip(ref HeaderTip);

      UpdateHeaderIndex(header);
    }

    internal List<Header> GetLocator()
    {
      Header header = HeaderTip;
      List<Header> locator = new();
      int heightCheckpoint = Token.GetCheckpoints().Keys.Max();
      int depth = 0;
      int nextLocationDepth = 0;

      while (header.Height > heightCheckpoint)
      {
        if (depth == nextLocationDepth)
        {
          locator.Add(header);
          nextLocationDepth = 2 * nextLocationDepth + 1;
        }

        depth++;
        header = header.HeaderPrevious;
      }

      locator.Add(header);

      return locator;
    }

    public bool TryReadHeader(
      byte[] headerHash,
      out Header header)
    {
      int key = BitConverter.ToInt32(headerHash, 0);

      lock (HeaderIndex)
      {
        if (HeaderIndex.TryGetValue(
          key, 
          out List<Header> headers))
        {
          foreach (Header h in headers)
          {
            if (headerHash.IsEqual(h.Hash))
            {
              header = h;
              return true;
            }
          }
        }
      }

      header = null;
      return false;
    }

    void UpdateHeaderIndex(Header header)
    {
      int keyHeader = BitConverter.ToInt32(header.Hash, 0);

      lock (HeaderIndex)
      {
        if (!HeaderIndex.TryGetValue(keyHeader, out List<Header> headers))
        {
          headers = new List<Header>();
          HeaderIndex.Add(keyHeader, headers);
        }

        headers.Add(header);
      }
    }


    public List<Header> GetHeaders(
      IEnumerable<byte[]> locatorHashes,
      int count,
      byte[] stopHash)
    {
      foreach (byte[] hash in locatorHashes)
      {
        if (TryReadHeader(hash, out Header header))
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


    public bool TryLock()
    {
      lock (LOCK_IsBlockchainLocked)
      {
        if (IsBlockchainLocked)
        {
          return false;
        }

        IsBlockchainLocked = true;

        return true;
      }
    }

    public void ReleaseLock()
    {
      lock (LOCK_IsBlockchainLocked)
      {
        IsBlockchainLocked = false;
      }
    }

    public void ArchiveBlock(
      Block block,
      int intervalImage)
    {
      while (true)
      {
        try
        {
          FileBlockArchive.Write(
            block.Buffer,
            0,
            block.IndexBufferStop);

          FileBlockArchive.Flush();

          break;
        }
        catch (Exception ex)
        {
          Console.WriteLine(
            $"{ex.GetType().Name} when writing block {block} to " +
            $"file {FileBlockArchive.Name}:\n" +
            $"{ex.Message}\n " +
            $"Try again in 10 seconds ...");

          Thread.Sleep(10000);
        }
      }

      CountBytesArchive += block.IndexBufferStop;

      if (CountBytesArchive >= SIZE_BLOCK_ARCHIVE_BYTES)
      {
        FileBlockArchive.Dispose();

        IndexBlockArchiveInsert += 1;

        if (IndexBlockArchiveInsert % intervalImage == 0)
        {
          string pathImage = IsFork ? 
            Path.Combine(NameFork, NameImage) : 
            NameImage;

          CreateImage(
            IndexBlockArchiveInsert, 
            pathImage);
        }

        CreateBlockArchive(IndexBlockArchiveInsert);
      }
    }
    
    void OpenBlockArchive(int index)
    {
      $"Open BlockArchive {index}.".Log(LogFile);

      string pathFileArchive = Path.Combine(
        PathBlockArchive,
        index.ToString());

      FileBlockArchive = new FileStream(
       pathFileArchive,
       FileMode.Append,
       FileAccess.Write,
       FileShare.None,
       bufferSize: 65536);
    }

    void CreateImage(
      int indexBlockArchive,
      string pathImage)
    {
      string pathimageOld = pathImage + NameImageOld;

      try
      {
        while (true)
        {
          try
          {
            Directory.Delete(pathimageOld, true);

            break;
          }
          catch (DirectoryNotFoundException)
          {
            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              "Cannot delete directory old due to {0}:\n{1}",
              ex.GetType().Name,
              ex.Message);

            Thread.Sleep(3000);
          }
        }

        while (true)
        {
          try
          {
            Directory.Move(
              pathImage,
              pathimageOld);

            break;
          }
          catch (DirectoryNotFoundException)
          {
            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              "Cannot move new image to old due to {0}:\n{1}",
              ex.GetType().Name,
              ex.Message);

            Thread.Sleep(3000);
          }
        }

        Directory.CreateDirectory(pathImage);

        string pathimageHeaderchain = Path.Combine(
          pathImage,
          "ImageHeaderchain");

        using (var fileImageHeaderchain =
          new FileStream(
            pathimageHeaderchain,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536))
        {
          Header header = HeaderGenesis.HeaderNext;

          while (header != null)
          {
            byte[] headerBytes = header.GetBytes();

            fileImageHeaderchain.Write(
              headerBytes, 0, headerBytes.Length);

            header = header.HeaderNext;
          }
        }

        File.WriteAllBytes(
          Path.Combine(
            pathImage,
            FileNameIndexBlockArchiveImage),
          BitConverter.GetBytes(indexBlockArchive));

        using (FileStream stream = new FileStream(
           Path.Combine(pathImage, "MapBlockHeader"),
           FileMode.Create,
           FileAccess.Write))
        {
          foreach (KeyValuePair<byte[], int> keyValuePair
            in BlockIndex)
          {
            stream.Write(
              keyValuePair.Key,
              0,
              keyValuePair.Key.Length);

            byte[] valueBytes = BitConverter.GetBytes(
              keyValuePair.Value);

            stream.Write(valueBytes, 0, valueBytes.Length);
          }
        }

        Token.CreateImage(pathImage);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"{ex.GetType().Name}:\n{ex.Message}");
      }
    }

    void CreateBlockArchive(int index)
    {
      string pathFileArchive = Path.Combine(
        PathBlockArchive,
        IsFork ? NameFork : "",
        index.ToString());

      FileBlockArchive = new FileStream(
       pathFileArchive,
       FileMode.Create,
       FileAccess.Write,
       FileShare.None,
       bufferSize: 65536);

      CountBytesArchive = 0;
    }


    public bool IsFork;
    public double DifficultyOld;

    internal bool TryFork(Header headerAncestor)
    {
      DifficultyOld = HeaderTip.Difficulty;

      LoadImage(
         headerAncestor.Height,
         headerAncestor.Hash);

      if (HeaderTip.Height != headerAncestor.Height)
      {
        IsFork = false;
        LoadImage();
      }
      else
      {
        IsFork = true;
      }

      return IsFork;
    }

    internal void DismissFork()
    {
      IsFork = false;
    }

    internal void Reorganize()
    {
      string pathImageFork = Path.Combine(
        NameFork, 
        NameImage);

      if(Directory.Exists(pathImageFork))
      {
        while (true)
        {
          try
          {
            Directory.Delete(
              NameImage,
              true);

            Directory.Move(
              pathImageFork,
              NameImage);

            break;
          }
          catch (DirectoryNotFoundException)
          {
            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              "{0} when attempting to delete directory:\n{1}",
              ex.GetType().Name,
              ex.Message);

            Thread.Sleep(3000);
          }
        }
      }
      
      string pathImageForkOld = Path.Combine(
        NameFork,
        NameImage,
        NameImageOld);

      string pathImageOld = Path.Combine(
        NameImage,
        NameImageOld);

      if (Directory.Exists(pathImageForkOld))
      {
        while (true)
        {
          try
          {
            Directory.Delete(
              pathImageOld,
              true);

            Directory.Move(
              pathImageForkOld,
              pathImageOld);

            break;
          }
          catch (DirectoryNotFoundException)
          {
            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              "{0} when attempting to delete directory:\n{1}",
              ex.GetType().Name,
              ex.Message);

            Thread.Sleep(3000);
          }
        }
      }

      string pathBlockArchiveFork = PathBlockArchive + "Fork";
      var dirArchiveFork = new DirectoryInfo(pathBlockArchiveFork);

      string filename = Path.GetFileName(FileBlockArchive.Name);
      FileBlockArchive.Dispose();
      
      foreach (FileInfo archiveFork in dirArchiveFork.GetFiles())
      {
        archiveFork.MoveTo(PathBlockArchive);
      }

      OpenBlockArchive(IndexBlockArchiveInsert);

      Directory.Delete(pathBlockArchiveFork);
      DismissFork();
    }
  }
}
