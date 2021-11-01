using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Diagnostics;



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

    object LOCK_IsBlockchainLocked = new();
    bool IsBlockchainLocked;
        
    public int IndexBlockArchive;

    public const int COUNT_LOADER_TASKS = 4;
    int SIZE_BLOCK_ARCHIVE_BYTES = 0x1000000;
    const int UTXOIMAGE_INTERVAL_LOADER = 100;

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
        $"Block tip: {HeaderTip}\n" +
        $"Timestamp: {DateTimeOffset.FromUnixTimeSeconds(HeaderTip.UnixTimeSeconds)}\n" +
        $"Age: {ageBlock}\n";
    }

    byte[] HashStopLoading;

    public void LoadImage()
    {
      LoadImage(0, new byte[32]);
    }
        
    void LoadImage(int heightMax, byte[] hashStopLoading)
    {
      string pathImage = NameImage;
      
      while(true)
      {
        if (
          !TryLoadImageFile(pathImage) ||
          (heightMax > 0 && HeaderTip.Height > heightMax))
        {
          Reset();

          if (pathImage == NameImage)
          {
            pathImage += NameOld;
            continue;
          }
        }

        Console.WriteLine(
          $"Start loading block from blockArchive {IndexBlockArchiveLoad}.");

        if (TryLoadBlocks(hashStopLoading))
        {
          return;
        }
      }
    }

    bool TryLoadImageFile(string pathImage)
    {
      try
      {
        LoadImageHeaderchain(pathImage);

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

        return false;
      }
    }

    void Reset()
    {
      HeaderTip = HeaderRoot;

      HeaderIndex.Clear();
      UpdateHeaderIndex(HeaderRoot);

      Token.Reset();

      BlockIndex.Clear();

      IndexBlockArchiveLoad = 0;
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
        

    public void InsertBlock(
      Block block,
      int intervalArchiveImage = 3)
    {
      InsertHeader(block.Header);

      Token.InsertBlock(
        block,
        IndexBlockArchive);

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


    Dictionary<int, Thread> ThreadsSleeping = new();

    public bool TryLoadBlocks(byte[] hashStopLoading)
    {
      "Start archive loader".Log(LogFile);

      HashStopLoading = hashStopLoading;
      IndexBlockArchiveInsert = IndexBlockArchiveLoad;

      Parallel.For(
        0,
        COUNT_LOADER_TASKS,
        i => StartLoader());

      return IsInserterSuccess;
    }

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
            ThreadsSleeping.Remove(blockLoad.Index);
          }
        }

        lock (LOCK_IndexBlockLoadInsert)
        {
          if (blockLoad.Index != IndexBlockArchiveInsert)
          {
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
          return;
        }

        if (blockLoad.CountBytes < SIZE_BLOCK_ARCHIVE_BYTES)
        {
          CountBytesArchive = blockLoad.CountBytes;

          OpenBlockArchive(blockLoad.Index);
          return;
        }

        IndexBlockArchiveInsert += 1;

        if (IndexBlockArchive % UTXOIMAGE_INTERVAL_LOADER == 0)
        {
          CreateImage(
            IndexBlockArchive,
            NameImage);
        }

        if (ThreadsSleeping.TryGetValue(
          IndexBlockArchiveInsert,
          out Thread threadSleeping))
        {
          threadSleeping.Interrupt();
        }
      }
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
            IndexBlockArchive);

          if (block.Header.Hash.IsEqual(HashStopLoading))
          {
            FileBlockArchive.Dispose();

            CreateBlockArchive(blockLoad.Index);

            foreach (Block blockArchiveFork in blockLoad.Blocks)
            {
              ArchiveBlock(blockArchiveFork, -1);

              if (blockArchiveFork == block)
              {
                break;
              }
            }
          }
        }

        Debug.WriteLine(
          $"Loaded blockchain height: {HeaderTip.Height}, " +
          $"blockload Index: {blockLoad.Index}");

        return true;
      }
      catch (ProtocolException)
      {
        File.Delete(
          Path.Combine(
            PathBlockArchive,
            blockLoad.Index.ToString()));

        return false;
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
