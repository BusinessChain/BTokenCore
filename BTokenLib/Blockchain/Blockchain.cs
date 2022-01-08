using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Security.Cryptography;
using System.Diagnostics;



namespace BTokenLib
{
  public partial class Blockchain
  {
    Token Token;
    public BlockArchiver Archiver;

    Header HeaderGenesis;
    public Header HeaderTip;

    Dictionary<int, List<Header>> HeaderIndex = new();

    static string NameFork = "Fork";
    static string NameImage = "Image";
    static string NameImageOld = "ImageOld";

    string FileNameIndexBlockArchiveLoad = "IndexBlockArchive";

    object LOCK_IsBlockchainLocked = new();
    bool IsBlockchainLocked;

    const int UTXOIMAGE_INTERVAL_LOADER = 200;

    StreamWriter LogFile;



    public Blockchain(
      Token token,
      string pathBlockArchive)
    {
      Token = token;

      Archiver = new BlockArchiver(
        this, 
        token,
        pathBlockArchive);

      LogFile = new StreamWriter(
        Path.Combine(Token.GetName() + "LogBlockchain"), 
        false);

      InizializeHeaderchain();
    }

    public bool TryLock()
    {
      lock (LOCK_IsBlockchainLocked)
      {
        if (IsBlockchainLocked)
          return false;

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

    public string GetStatus()
    {
      var ageBlock = TimeSpan.FromSeconds(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - HeaderTip.UnixTimeSeconds);

      return 
        "\n Status Blockchain:\n" +
        $"Height: {HeaderTip.Height}\n" +
        $"Block tip: {HeaderTip}\n" +
        $"Difficulty Tip: {HeaderTip.Difficulty}\n" +
        $"Acc. Difficulty: {HeaderTip.DifficultyAccumulated}\n" +
        $"Timestamp: {DateTimeOffset.FromUnixTimeSeconds(HeaderTip.UnixTimeSeconds)}\n" +
        $"Age: {ageBlock}\n";
    }


    public void LoadImage()
    {
      LoadImage(
        new byte[32],
        0);
    }
        
    void LoadImage(
      byte[] hashStopLoading,
      int heightStopLoading)
    {
      string pathImage = NameImage;

      while (true)
      {
        InizializeHeaderchain();
        Token.Reset();
        int indexBlockArchiveLoad;

        try
        {
          LoadImageHeaderchain(pathImage);

          if (
            heightStopLoading > 0 && 
            HeaderTip.Height > heightStopLoading)
          {
            throw new ProtocolException(
              $"Headerchain not loading up to desired height {heightStopLoading}.");
          }

          Token.LoadImage(pathImage);

          indexBlockArchiveLoad = BitConverter.ToInt32(
            File.ReadAllBytes(
              Path.Combine(
                pathImage,
                FileNameIndexBlockArchiveLoad)),
            0);
        }
        catch
        {
          InizializeHeaderchain();
          Token.Reset();
          indexBlockArchiveLoad = 1;

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

        if (Archiver.TryLoadBlocks(
          indexBlockArchiveLoad,
          hashStopLoading))
        {
          return;
        }
      }
    }


    void LoadImageHeaderchain(string pathImage)
    {
      byte[] bytesHeaderImage = File.ReadAllBytes(
        Path.Combine(pathImage, "ImageHeaderchain"));

      int index = 0;

      while (index < bytesHeaderImage.Length)
      {
        Header header = Token.ParseHeader(
         bytesHeaderImage,
         ref index,
         SHA256.Create());

        header.IndexBlockArchive = BitConverter.ToInt32(
          bytesHeaderImage, index);

        index += 4;

        header.StartIndexBlockArchive = BitConverter.ToInt32(
          bytesHeaderImage, index);

        index += 4;

        header.CountBlockBytes = BitConverter.ToInt32(
          bytesHeaderImage, index);

        index += 4;

        InsertHeader(header);
      }
    }


    void InizializeHeaderchain()
    {
      HeaderIndex.Clear();

      HeaderGenesis = Token.CreateHeaderGenesis();

      HeaderTip = HeaderGenesis;

      IndexingHeaderTip();
    }


    internal List<Header> GetLocator()
    {
      Header header = HeaderTip;
      List<Header> locator = new();
      int heightCheckpoint = Token.GetCheckpointHeight();
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


    public void InsertBlock(
      Block block,
      int intervalArchiveImage = 3)
    {
      InsertHeader(block.Header);

      bool flagCreateImage = Archiver.ArchiveBlockFlagCreateImage(
        block,
        intervalArchiveImage);

      Token.InsertBlock(block);

      if(flagCreateImage)
      {
        string pathImage = IsFork ?
          Path.Combine(NameFork, NameImage) : NameImage;

        CreateImage(
          Archiver.IndexBlockArchiveInsert,
          pathImage);
      }
    }

    public void InsertHeader(Header header)
    {
      header.AppendToHeader(HeaderTip);

      HeaderTip.HeaderNext = header;
      HeaderTip = header;

      IndexingHeaderTip();
    }

    public bool TryReadHeader(
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

    void IndexingHeaderTip()
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

    void CreateImage(
      int indexBlockArchive,
      string pathImage)
    {
      try
      {
        while (true)
        {
          try
          {
            Directory.Delete(NameImageOld, true);
            break;
          }
          catch (DirectoryNotFoundException)
          {
            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              $"Cannot delete directory old due to " +
              $"{ex.GetType().Name}:\n{ex.Message}");

            Thread.Sleep(3000);
          }
        }

        while (true)
        {
          try
          {
            Directory.Move(
              pathImage,
              NameImageOld);

            break;
          }
          catch (DirectoryNotFoundException)
          {
            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              $"Cannot move new image to old due to " +
              $"{ex.GetType().Name}:\n{ex.Message}");

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
              BitConverter.GetBytes(header.CountBlockBytes);

            fileImageHeaderchain.Write(
              bytesCountBlockBytes, 0, bytesCountBlockBytes.Length);

            header = header.HeaderNext;
          }
        }

        File.WriteAllBytes(
          Path.Combine(
            pathImage,
            FileNameIndexBlockArchiveLoad),
          BitConverter.GetBytes(indexBlockArchive));

        Token.CreateImage(pathImage);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"{ex.GetType().Name}:\n{ex.Message}");
      }
    }

    public bool IsFork;
    public double DifficultyOld;

    internal bool TryFork(Header headerAncestor)
    {
      DifficultyOld = HeaderTip.Difficulty;

      LoadImage(
        headerAncestor.Hash,
        headerAncestor.Height);

      IsFork = HeaderTip.Height == headerAncestor.Height;

      if (!IsFork)
        LoadImage();

      return IsFork;
    }

    internal void DismissFork()
    {
      IsFork = false;
    }

    internal void FinalizeBlockchain(
      bool flagBlockchainCorrupted)
    {
      if (IsFork)
      {
        if (HeaderTip.Difficulty > DifficultyOld)
          Archiver.Reorganize();
        else
        {
          IsFork = false;
          flagBlockchainCorrupted = true;
        }
      }

      if (flagBlockchainCorrupted)
      {
        "Synchronization was abort. Reload Image.".Log(LogFile);
        LoadImage();
      }
    }
  }
}
