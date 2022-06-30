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

    object LOCK_IsBlockchainLocked = new();
    bool IsBlockchainLocked;



    public Blockchain(Token token)
    {
      Token = token;
      InitializeHeaderchain();
    }

    public void InitializeHeaderchain()
    {
      HeaderGenesis = Token.CreateHeaderGenesis();

      HeaderTip = HeaderGenesis;

      HeaderIndex.Clear();
      IndexingHeaderTip();
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
           
    public void CreateImageHeaderchain(string path)
    {
      using (FileStream fileImageHeaderchain = new(
          path,
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
    }


    public void LoadImageHeaderchain(
      string pathImage, 
      int heightMax)
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

      if (HeaderTip.Height > heightMax)
        throw new ProtocolException(
          $"Image higher than desired height {heightMax}.");
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

    public void InsertHeader(Header header)
    {
      header.AppendToHeader(HeaderTip);      

      HeaderTip.HeaderNext = header;
      HeaderTip = header;

      IndexingHeaderTip();
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