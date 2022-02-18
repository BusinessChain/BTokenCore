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

    Header HeaderGenesis;
    public Header HeaderTip;

    Dictionary<int, List<Header>> HeaderIndex = new();

    static string NameFork = "Fork";
    static string NameImage = "Image";
    static string NameImageOld = "ImageOld";

    string PathImage;
    string PathImageOld;
    string PathImageFork;
    string PathImageForkOld;

    string PathRootSystem;

    object LOCK_IsBlockchainLocked = new();
    bool IsBlockchainLocked;

    StreamWriter LogFile;



    public Blockchain(Token token)
    {
      Token = token;
      PathRootSystem = token.GetName();

      Directory.CreateDirectory(PathRootSystem);

      PathImage = Path.Combine(
        PathRootSystem,
        NameImage);

      PathImageOld = Path.Combine(
        PathRootSystem,
        NameImageOld);

      PathImageFork = Path.Combine(
        PathRootSystem,
        NameFork,
        NameImage);

      PathImageForkOld = Path.Combine(
        PathRootSystem,
        NameFork,
        NameImageOld);

      LogFile = new StreamWriter(
        Path.Combine(PathRootSystem, "LogBlockchain"),
        false);

      InitializeHeaderchain();
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
        IsBlockchainLocked = false;
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
      LoadImage(0);
    }
        
    public void LoadImage(int heightMax)
    {
      $"Load image {this}.".Log(LogFile);

      string pathImageLoad = PathImage;

      while (true)
      {
        InitializeHeaderchain();
        Token.Reset();

        if (heightMax == 0)
          return;

        try
        {
          LoadImageHeaderchain(pathImageLoad);

          if (HeaderTip.Height > heightMax)
            throw new ProtocolException(
              $"Image higher than desired height {heightMax}.");

          Token.LoadImage(pathImageLoad);
        }
        catch
        {
          InitializeHeaderchain();
          Token.Reset();

          if (pathImageLoad == PathImage)
          {
            pathImageLoad = PathImageOld;
            continue;
          }
        }

        return;
      }
    }


    public void LoadImageHeaderchain(string pathImage)
    {
      byte[] bytesHeaderImage = File.ReadAllBytes(
        Path.Combine(pathImage, "ImageHeaderchain"));

      int index = 0;

      while (index < bytesHeaderImage.Length)
      {
        Header header = Token.ParseHeader(
         bytesHeaderImage,
         ref index);

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


    public void InitializeHeaderchain()
    {
      HeaderGenesis = Token.CreateHeaderGenesis();

      HeaderTip = HeaderGenesis;

      HeaderIndex.Clear();
      IndexingHeaderTip();
    }


    internal List<Header> GetLocator()
    {
      Header header = HeaderTip;
      List<Header> locator = new();
      int depth = 0;
      int nextLocationDepth = 0;

      while (header != null)
      {
        if (depth == nextLocationDepth)
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
      bool flagCreateImage)
    {
      InsertHeader(block.Header);
      Token.InsertBlock(block);

      if (flagCreateImage)
        CreateImage();
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


    public void CreateImage()
    {
      string pathImage = IsFork ?
        Path.Combine(NameFork, NameImage) : NameImage;

      CreateImage(pathImage);
    }

    void CreateImage(string pathImage)
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

        Token.CreateImage(pathImage);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"{ex.GetType().Name}:\n{ex.Message}");
      }
    }

    public bool IsFork;
    public double DifficultyOld;

    internal void Reorganize()
    {
      TryMoveDirectory(PathImageFork, PathImage);
      TryMoveDirectory(PathImageForkOld, PathImageOld);
    }

    bool TryMoveDirectory(string pathSource, string pathDest)
    {
      if (!Directory.Exists(pathSource))
        return false;

      while (true)
        try
        {
          if (Directory.Exists(pathDest))
            Directory.Delete(
              pathDest,
              true);

          Directory.Move(
            pathSource,
            pathDest);

          return true;
        }
        catch (Exception ex)
        {
          Console.WriteLine(
            $"{ex.GetType().Name} when attempting " +
            $"to delete directory:\n{ex.Message}");

          Thread.Sleep(3000);
        }
    }

    public override string ToString()
    {
      return Token.GetName();
    }
  }
}