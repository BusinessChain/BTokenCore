using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Threading.Tasks.Dataflow;
using System.Diagnostics;



namespace BTokenCore.Chaining
{
  partial class Blockchain
  {
    public Network Network;
    public Token Token;

    Header HeaderGenesis;
    internal Header HeaderTip;
    internal double Difficulty;
    internal int Height;

    readonly object HeaderIndexLOCK = new object();
    Dictionary<int, List<Header>> HeaderIndex;

    string NameFork = "Fork";
    string NameImage = "Image";
    string NameOld = "Old";

    string FileNameIndexBlockArchiveImage = "IndexBlockArchive";
    string PathBlockArchive = "J:\\BlockArchivePartitioned";
    string PathBlockArchiveFork = "J:\\BlockArchivePartitionedFork";

    readonly object LOCK_IsBlockchainLocked = new object();
    bool IsBlockchainLocked;
    
    long UTCTimeStartMerger = 
      DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    
    public int IndexBlockArchive;

    byte[] HashRootFork;
    public const int COUNT_LOADER_TASKS = 4;
    int SIZE_BLOCK_ARCHIVE_BYTES = 0x1000000;
    const int UTXOIMAGE_INTERVAL_LOADER = 400;

    int IndexBlockArchiveQueue;

    string PathRoot;
    StreamWriter LogFile;

    readonly object LOCK_IndexBlockArchiveLoad = new object();
    int IndexBlockArchiveLoad;




    public Blockchain(
      Network network,
      Token token)
    {
      Network = network;
      Network.Blockchain = this;

      Token = token;

      PathRoot = Token.GetName();

      HeaderGenesis = token.GetHeaderGenesis();
      HeaderTip = HeaderGenesis;
            
      HeaderIndex = new Dictionary<int, List<Header>>();
      UpdateHeaderIndex(HeaderGenesis);
      
      DirectoryInfo DirectoryBlockArchive =
          Directory.CreateDirectory(PathBlockArchive);

      LogFile = new StreamWriter(
        Path.Combine(PathRoot + "logArchiver"), 
        false);
    }


    public string GetStatus()
    {
      var ageBlock = TimeSpan.FromSeconds(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - HeaderTip.UnixTimeSeconds);

      string statusBlockchain = string.Format(
        "Height: {0}\n" +
        "Block tip: {1}\n" +
        "Timestamp: {2}\n" +
        "Age: {3}\n",
        Height,
        HeaderTip.Hash.ToHexString(),
        DateTimeOffset.FromUnixTimeSeconds(HeaderTip.UnixTimeSeconds),
        ageBlock);

      string statusUTXO = Token.GetStatus();

      return statusBlockchain + statusUTXO;
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
        (heightMax > 0 && Height > heightMax))
        {
          if (pathImage == NameImage)
          {
            pathImage += NameOld;

            continue;
          }

          Reset();
        }
        
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
        LoadImageHeaderchain(pathImage);

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

      Header headerPrevious = HeaderGenesis;
      
      while(indexBytesHeaderImage < bytesHeaderImage.Length)
      {
        Header header = Token.ParseHeader(
         bytesHeaderImage,
         ref indexBytesHeaderImage);

        if (!header.HashPrevious.IsEqual(
          headerPrevious.Hash))
        {
          throw new ProtocolException(
            "Header image does not link to genesis header.");
        }

        header.HeaderPrevious = headerPrevious;

        Token.ValidateHeader(
          header, 
          Height + 1);

        InsertHeader(header);

        headerPrevious = header;
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

        MapBlockToArchiveIndex.Add(key, value);
      }
    }
        
    void Reset()
    {
      HeaderTip = HeaderGenesis;
      Height = 0;
      Difficulty = HeaderGenesis.Difficulty;
      
      HeaderIndex.Clear();
      UpdateHeaderIndex(HeaderGenesis);

      Token.Reset();
    }
    
    public void ValidateHeaders(Header header)
    {
      int height = Height + 1;

      do
      {
        Token.ValidateHeader(header, height);
        header = header.HeaderNext;
        height += 1;
      } while (header != null);
    }

    internal void GetStateAtHeader(
      Header headerAncestor,
      out int heightAncestor,
      out double difficultyAncestor)
    {
      heightAncestor = Height - 1;
      difficultyAncestor = Difficulty - HeaderTip.Difficulty;

      Header header = HeaderTip.HeaderPrevious;

      while (header != headerAncestor)
      {
        difficultyAncestor -= header.Difficulty;
        heightAncestor -= 1;
        header = header.HeaderPrevious;
      }
    }

    
    public bool TryInsertBlock(
      Block block,
      bool flagValidateHeader)
    {
      try
      {
        block.Header.HeaderPrevious = HeaderTip;

        if (flagValidateHeader)
        {
          Token.ValidateHeader(block.Header, Height + 1);
        }

        Token.InsertBlock(
          block,
          IndexBlockArchive);

        InsertHeader(block.Header);

        return true;
      }
      catch(Exception ex)
      {
        Debug.WriteLine(
          "{0} when inserting block {1} in blockchain:\n {2}",
          ex.GetType().Name,
          block.Header.Hash.ToHexString(),
          ex.Message);

        return false;
      }
    }

    void InsertHeader(Header header)
    {
      HeaderTip.HeaderNext = header;
      HeaderTip = header;

      Difficulty += header.Difficulty;
      Height += 1;
    }



    internal List<Header> GetLocator()
    {
      Header header = HeaderTip;
      var locator = new List<Header>();
      int height = Height;
      int heightCheckpoint = Token.GetCheckpoints().Keys.Max();
      int depth = 0;
      int nextLocationDepth = 0;

      while (height > heightCheckpoint)
      {
        if (depth == nextLocationDepth)
        {
          locator.Add(header);
          nextLocationDepth = 2 * nextLocationDepth + 1;
        }

        depth++;
        height--;
        header = header.HeaderPrevious;
      }

      locator.Add(header);

      return locator;
    }
          
                

    public Dictionary<byte[], int> MapBlockToArchiveIndex =
      new Dictionary<byte[], int>(
        new EqualityComparerByteArray());

       
    
    internal bool ContainsHeader(byte[] headerHash)
    {
      return TryReadHeader(
        headerHash,
        out Header header);
    }

    bool TryReadHeader(
      byte[] headerHash,
      out Header header)
    {
      SHA256 sHA256 = SHA256.Create();

      return TryReadHeader(
        headerHash,
        sHA256,
        out header);
    }

    bool TryReadHeader(
      byte[] headerHash,
      SHA256 sHA256,
      out Header header)
    {
      int key = BitConverter.ToInt32(headerHash, 0);

      lock (HeaderIndexLOCK)
      {
        if (HeaderIndex.TryGetValue(key, out List<Header> headers))
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

      lock (HeaderIndexLOCK)
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
          List<Header> headers = new List<Header>();

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


    internal bool TryLock()
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

    internal void ReleaseLock()
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

    BufferBlock<BlockLoad> QueueLoader =
      new BufferBlock<BlockLoad>();
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
          !blockLoad.Blocks.Any() ||
          !HeaderTip.Hash.IsEqual(
            blockLoad.Blocks.First().Header.HashPrevious))
        {
          CreateBlockArchive();
          return;
        }

        foreach (Block block in blockLoad.Blocks)
        {
          if (!TryInsertBlock(
            block,
            flagValidateHeader: true))
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

        Debug.WriteLine(
          "{0},{1},{2}",
          Height,
          blockLoad.Index,
          DateTimeOffset.UtcNow.ToUnixTimeSeconds() - UTCTimeStartMerger);

        if (blockLoad.CountBytes < SIZE_BLOCK_ARCHIVE_BYTES)
        {
          CountBytesArchive = blockLoad.CountBytes;
          OpenBlockArchive();

          return;
        }

        IndexBlockArchive += 1;
        
        if (
          IndexBlockArchive % UTXOIMAGE_INTERVAL_LOADER == 0)
        {
          CreateImage(
            IndexBlockArchive,
            NameImage);
        }
      }
    }


    bool IsInserterCompleted;
    Dictionary<int, BlockLoad> QueueBlockArchives =
      new Dictionary<int, BlockLoad>();
    readonly object LOCK_QueueBlockArchives = new object();

    async Task StartLoader()
    {
      IBlockParser parser = Token.CreateParser();

    LABEL_LoadBlockArchive:

      var blockLoad = new BlockLoad();

      lock (LOCK_IndexBlockArchiveLoad)
      {
        blockLoad.Index = IndexBlockArchiveLoad;
        IndexBlockArchiveLoad += 1;
      }

      string pathFile = Path.Combine(
        PathBlockArchive,
        blockLoad.Index.ToString());

      try
      {
        byte[] bytesFile = File.ReadAllBytes(pathFile);

        int startIndex = 0;

        while (startIndex < bytesFile.Length)
        {
          Block block = parser.ParseBlock(
            bytesFile,
            ref startIndex);

          blockLoad.InsertBlock(block);
        }

        blockLoad.CountBytes = bytesFile.Length;
        blockLoad.IsInvalid = false;
      }
      catch (Exception ex)
      {
        blockLoad.IsInvalid = true;

        string.Format(
          "Loader throws exception {0} \n" +
          "when parsing file {1}",
          pathFile,
          ex.Message)
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
            block.Buffer.Length);

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

      CountBytesArchive += block.Buffer.Length;

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
            in MapBlockToArchiveIndex)
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


    bool IsFork;

    internal async Task<bool> TryFork(
      int heightAncestor, 
      byte[] hashAncestor)
    {
      IsFork = true;

      await LoadImage(
         heightAncestor,
         hashAncestor);

      if(Height == heightAncestor)
      {
        return true;
      }

      IsFork = false;
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

      var dirArchiveFork = new DirectoryInfo(PathBlockArchiveFork);

      string filename = Path.GetFileName(FileBlockArchive.Name);
      FileBlockArchive.Dispose();
      
      foreach (FileInfo archiveFork in dirArchiveFork.GetFiles())
      {
        archiveFork.MoveTo(PathBlockArchive);
      }

      OpenBlockArchive();

      Directory.Delete(PathBlockArchiveFork);
      DismissFork();
    }
  }
}
