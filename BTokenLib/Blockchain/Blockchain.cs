using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Threading.Tasks.Dataflow;
using System.Diagnostics;
using System.Collections.Concurrent;



namespace BTokenLib
{
  public partial class Blockchain
  {
    public Token Token;

    Header HeaderRoot;
    public Header HeaderTip;

    Dictionary<int, List<Header>> HeaderIndex = new();


    // Brauche ich das?
    Dictionary<byte[], int> BlockIndex =
      new(new EqualityComparerByteArray());

    string NameFork = "Fork";
    string NameImage = "Image";
    string NameOld = "Old";

    string FileNameIndexBlockArchiveImage = "IndexBlockArchive";
    string PathBlockArchive;

    readonly object LOCK_IsBlockchainLocked = new();
    bool IsBlockchainLocked;
    
    long UTCTimeStartMerger = 
      DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    
    public int IndexBlockArchive;

    byte[] HashRootFork;
    public const int COUNT_LOADER_TASKS = 4;
    int SIZE_BLOCK_ARCHIVE_BYTES = 0x1000000;
    const int UTXOIMAGE_INTERVAL_LOADER = 100;

    int IndexBlockArchiveQueue;

    StreamWriter LogFile;

    readonly object LOCK_IndexBlockArchiveLoad = new();
    int IndexBlockArchiveLoad;



    public Blockchain(
      Token token,
      string pathBlockArchive)
    {
      Token = token;

      PathBlockArchive = pathBlockArchive;

      HeaderRoot = token.CreateHeaderGenesis();
      HeaderRoot.Height = 0;
      HeaderRoot.DifficultyAccumulated = HeaderRoot.Difficulty;

      HeaderTip = HeaderRoot;

      UpdateHeaderIndex(HeaderRoot);
      
      DirectoryInfo DirectoryBlockArchive =
          Directory.CreateDirectory(PathBlockArchive);

      LogFile = new StreamWriter(
        Path.Combine(Token.GetName() + "LogArchiver"), 
        false);
    }


    public string GetStatus()
    {
      var ageBlock = TimeSpan.FromSeconds(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - HeaderTip.UnixTimeSeconds);

      return 
        "\n Status Blockchain:\n" +
        $"Height: {HeaderTip.Height}\n" +
        $"Block tip: {HeaderTip.Hash.ToHexString()}\n" +
        $"Timestamp: {DateTimeOffset.FromUnixTimeSeconds(HeaderTip.UnixTimeSeconds)}\n" +
        $"Age: {ageBlock}\n";
    }

    public async Task LoadImage()
    {
      await LoadImage(0, new byte[32]);
    }
        
    async Task LoadImage(
      int heightMax,
      byte[] hashRootFork)
    {
      string pathImage = NameImage;
      
      while(true)
      {
        Reset();

        if (!TryLoadImageFile(
          pathImage, 
          out int indexBlockArchiveImage) ||
        (heightMax > 0 && HeaderTip.Height > heightMax))
        {
          if (pathImage == NameImage)
          {
            pathImage += NameOld;

            continue;
          }

          Reset();
        }

        Console.WriteLine(
          "Start loading block from blockArchive {0}.", 
          indexBlockArchiveImage);

        if (await TryLoadBlocks(
          hashRootFork,
          indexBlockArchiveImage))
        {
          return;
        }
      }
    }


    bool TryLoadImageFile(
      string pathImage, 
      out int indexBlockArchiveImage)
    {
      try
      {
        Console.WriteLine("Load headerchain.");
        LoadImageHeaderchain(pathImage);

        Console.WriteLine("Load UTXO.");
        Token.LoadImage(pathImage);

        LoadMapBlockToArchiveData(
          File.ReadAllBytes(
            Path.Combine(pathImage, "MapBlockHeader")));

        indexBlockArchiveImage = BitConverter.ToInt32(
          File.ReadAllBytes(
            Path.Combine(
              pathImage,
              FileNameIndexBlockArchiveImage)),
          0);

        return true;
      }
      catch(Exception ex)
      {
        Console.WriteLine("{0} when loading image {1}:\n{2}",
          ex.GetType().Name,
          pathImage,
          ex.Message);

        indexBlockArchiveImage = 0;
        return false;
      }
    }

    void LoadImageHeaderchain(string pathImage)
    {
      string pathFile = Path.Combine(
        pathImage, 
        "ImageHeaderchain");


      int indexBytesHeaderImage = 0;
      byte[] bytesHeaderImage = File.ReadAllBytes(pathFile);

      HeaderTip = HeaderRoot;
      
      while(indexBytesHeaderImage < bytesHeaderImage.Length)
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
      HeaderTip = HeaderRoot;
      
      HeaderIndex.Clear();
      UpdateHeaderIndex(HeaderRoot);

      Token.Reset();
    }

    public void InsertBlock(
      Block block,
      int intervalArchiveImage = 3)
    {
      InsertHeader(block.Header);

      Token.InsertBlock(
        block,
        IndexBlockArchive);

      if (intervalArchiveImage > 0)
      {
        ArchiveBlock(
          block,
          intervalArchiveImage);
      }
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



    public async Task<bool> TryLoadBlocks(
      byte[] hashRootFork,
      int indexBlockArchive)
    {
      "Start archive loader".Log(LogFile);

      IsInserterCompleted = false;

      IndexBlockArchiveLoad = indexBlockArchive;
      IndexBlockArchiveQueue = indexBlockArchive;
      HashRootFork = hashRootFork;

      Task inserterTask = RunLoaderInserter();

      var loaderTasks = new Task[COUNT_LOADER_TASKS];

      Parallel.For(
        0,
        COUNT_LOADER_TASKS,
        i => loaderTasks[i] = StartLoader());

      await inserterTask;

      IsInserterCompleted = true;

      await Task.WhenAll(loaderTasks);

      return IsInserterSuccess;
    }

    BufferBlock<BlockLoad> QueueLoader = new();
    bool IsInserterSuccess;

    async Task RunLoaderInserter()
    {
      "Start archive inserter.".Log(LogFile);

      IsInserterSuccess = true;

      while (true)
      {
        BlockLoad blockLoad = await QueueLoader
          .ReceiveAsync()
          .ConfigureAwait(false);

        IndexBlockArchive = blockLoad.Index;

        if (
          blockLoad.IsInvalid ||
          blockLoad.Blocks.Count == 0 ||
          !blockLoad.Blocks[0].Header.HashPrevious.IsEqual(
            HeaderTip.Hash))
        {
          CreateBlockArchive();
          return;
        }

        foreach (Block block in blockLoad.Blocks)
        {
          try
          {
            InsertHeader(block.Header);

            Token.InsertBlock(
              block,
              IndexBlockArchive);
          }
          catch
          {
            File.Delete(
              Path.Combine(
                PathBlockArchive,
                blockLoad.Index.ToString()));

            IsInserterSuccess = false;
            return;
          }

          if (block.Header.Hash.IsEqual(HashRootFork))
          {
            FileBlockArchive.Dispose();

            CreateBlockArchive();

            foreach (Block blockArchiveFork in blockLoad.Blocks)
            {
              ArchiveBlock(blockArchiveFork, -1);

              if(blockArchiveFork == block)
              {
                return;
              }
            }
          }
        }

        long timeLoading =
          DateTimeOffset.UtcNow.ToUnixTimeSeconds() - UTCTimeStartMerger;

        Debug.WriteLine(
          $"{HeaderTip.Height},{blockLoad.Index},{timeLoading}");

        if (blockLoad.CountBytes < SIZE_BLOCK_ARCHIVE_BYTES)
        {
          CountBytesArchive = blockLoad.CountBytes;
          OpenBlockArchive();

          return;
        }

        IndexBlockArchive += 1;

        if (IndexBlockArchive % UTXOIMAGE_INTERVAL_LOADER == 0)
        {
          CreateImage(
            IndexBlockArchive,
            NameImage);
        }

        blockLoad.Blocks.Clear();
        PoolBlockLoad.Add(blockLoad);
      }
    }


    bool IsInserterCompleted;
    Dictionary<int, BlockLoad> QueueBlockArchives = new();
    readonly object LOCK_QueueBlockArchives = new object();
    ConcurrentBag<BlockLoad> PoolBlockLoad = new();

    async Task StartLoader()
    {
    LABEL_LoadBlockArchive:

      if (!PoolBlockLoad.TryTake(out BlockLoad blockLoad))
      {
        blockLoad = new BlockLoad(Token);
      }

      lock (LOCK_IndexBlockArchiveLoad)
      {
        blockLoad.Index = IndexBlockArchiveLoad++;
      }

      try
      {
        blockLoad.Parse(
          File.ReadAllBytes(Path.Combine(
            PathBlockArchive,
            blockLoad.Index.ToString())));
      }
      catch (Exception ex)
      {
        blockLoad.IsInvalid = true;

        ($"Loader throws {ex.GetType().Name} \n " +
          $"when parsing file: {ex.Message}")
        .Log(LogFile);
      }

      while (true)
      {
        if (IsInserterCompleted)
        {
          return;
        }

        if (QueueLoader.Count < COUNT_LOADER_TASKS)
        {
          lock (LOCK_QueueBlockArchives)
          {
            if (blockLoad.Index == IndexBlockArchiveQueue)
            {
              break;
            }

            if (QueueBlockArchives.Count <= COUNT_LOADER_TASKS)
            {
              QueueBlockArchives.Add(
                blockLoad.Index,
                blockLoad);

              if (blockLoad.IsInvalid)
              {
                return;
              }

              goto LABEL_LoadBlockArchive;
            }
          }
        }

        await Task.Delay(2000).ConfigureAwait(false);
      }

      while (true)
      {
        QueueLoader.Post(blockLoad);

        if (blockLoad.IsInvalid)
        {
          return;
        }

        // evt muss zuerst nächster blockLoad gezogen werden bevor zum Queue posten
        lock (LOCK_QueueBlockArchives)
        {
          IndexBlockArchiveQueue += 1;

          if (QueueBlockArchives.TryGetValue(
            IndexBlockArchiveQueue,
            out blockLoad))
          {
            QueueBlockArchives.Remove(
              blockLoad.Index);
          }
          else
          {
            goto LABEL_LoadBlockArchive;
          }
        }
      }
    }


    int CountBytesArchive;
    FileStream FileBlockArchive;

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
          string.Format(
            "{0} when writing block {1} to " +
            "file {2}: \n{3} \n" +
            "Try again in 10 seconds ...",
            ex.GetType().Name,
            block.Header.Hash.ToHexString(),
            FileBlockArchive.Name,
            ex.Message)
            .Log(LogFile);

          Thread.Sleep(10000);
        }
      }

      CountBytesArchive += block.IndexBufferStop;

      if (CountBytesArchive >= SIZE_BLOCK_ARCHIVE_BYTES)
      {
        FileBlockArchive.Dispose();

        IndexBlockArchive += 1;

        if (IndexBlockArchive % intervalImage == 0)
        {
          string pathImage = IsFork ? 
            Path.Combine(NameFork, NameImage) : 
            NameImage;

          CreateImage(
            IndexBlockArchive, 
            pathImage);
        }

        CreateBlockArchive();
      }
    }
    
    void OpenBlockArchive()
    {
      string.Format(
        "Open BlockArchive {0}",
        IndexBlockArchive)
        .Log(LogFile);

      string pathFileArchive = Path.Combine(
        PathBlockArchive,
        IndexBlockArchive.ToString());

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
      string pathimageOld = pathImage + NameOld;

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
          Header header = HeaderRoot.HeaderNext;

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
        Console.WriteLine(
          "{0}:\n{1}",
          ex.GetType().Name,
          ex.Message);
      }
    }

    void CreateBlockArchive()
    {
      string pathFileArchive = Path.Combine(
        PathBlockArchive,
        IsFork ? NameFork : "",
        IndexBlockArchive.ToString());

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

    internal async Task<bool> TryFork(
      Header headerAncestor)
    {
      DifficultyOld = HeaderTip.Difficulty;

      await LoadImage(
         headerAncestor.Height,
         headerAncestor.Hash);

      if(HeaderTip.Height == headerAncestor.Height)
      {
        IsFork = true;
        return true;
      }

      IsFork = false;

      await LoadImage();
      ReleaseLock();

      return false;
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
        NameOld);

      string pathImageOld = Path.Combine(
        NameImage,
        NameOld);

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

      OpenBlockArchive();

      Directory.Delete(pathBlockArchiveFork);
      DismissFork();
    }
  }
}
