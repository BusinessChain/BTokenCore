using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public Token TokenParent;
    public List<Token> TokenChilds = new();

    public Blockchain Blockchain;

    public Wallet Wallet;

    public Network Network;
    public UInt16 Port;

    protected List<TX> TXPool = new();



    static string NameFork = "Fork";
    static string NameImage = "Image";
    static string NameImageOld = "ImageOld";

    string PathImage;
    string PathImageOld;
    string PathImageFork;
    string PathImageForkOld;

    string PathRootToken;

    const int INTERVAL_BLOCKHEIGHT_IMAGE = 100;

    protected int CountBytesDataTokenBasis = 120;


    public Token(UInt16 port)
    {
      PathRootToken = GetName();
      Directory.CreateDirectory(PathRootToken);

      Blockchain = new Blockchain(this);

      Port = port;
      Network = new(this);

      Wallet = new();

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
    }

    public void Start()
    {
      //if (TokenParent != null)
      //  TokenParent.Start();

      LoadImage();
      Network.Start();
    }

    public virtual HeaderDownload CreateHeaderDownload()
    {
      return new HeaderDownload(Blockchain.GetLocator());
    }

    public string GetStatus()
    {
      string messageStatus = "";

      if (TokenParent != null)
        messageStatus += TokenParent.GetStatus();

      messageStatus +=
        $"\n\nStatus {GetName()}:\n" +
        $"{Blockchain.GetStatus()}" +
        $"{Network.GetStatus()}";

      return messageStatus;
    }

    public abstract List<string> GetSeedAddresses();

    bool IsLocked;

    public bool TryLock()
    {
      if (TokenParent != null)
        return TokenParent.TryLock();

      lock (this)
      {
        if (IsLocked)
          return false;

        IsLocked = true;
        return true;
      }
    }

    public void ReleaseLock()
    {
      if (TokenParent != null)
        TokenParent.ReleaseLock();
      else
        IsLocked = false;
    }

    public Token GetParentRoot()
    {
      if (TokenParent == null)
        return this;

      return TokenParent.GetParentRoot();
    }

    public abstract Header CreateHeaderGenesis();

    public virtual void CreateImageDatabase(string path) 
    { }

    internal void Reorganize()
    {
      PathImageFork.TryMoveDirectoryTo(PathImage);
      PathImageForkOld.TryMoveDirectoryTo(PathImageOld);
    }

    public void LoadImage()
    {
      LoadImage(int.MaxValue);
    }

    public void LoadImage(int heightMax)
    {
      string pathImageLoad = PathImage;

      while (true)
      {
        Reset();

        try
        {
          Blockchain.LoadImageHeaderchain(
            pathImageLoad, 
            heightMax);

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

        return;
      }
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

      Blockchain.CreateImageHeaderchain(
        Path.Combine(pathImage, "ImageHeaderchain"));

      CreateImageDatabase(pathImage);
      Wallet.CreateImage(pathImage);
    }

    public virtual void Reset()
    {
      Blockchain.InitializeHeaderchain();
    }

    public abstract Block CreateBlock();

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
      InsertBlock(block, null); 
    }

    public void InsertBlock(Block block, Network.Peer peer)
    {
      block.AppendToBlockchain(Blockchain);

      InsertInDatabase(block, peer);

      Blockchain.AppendHeader(block.Header);

      FeePerByteAverage =
        ((ORDER_AVERAGEING_FEEPERBYTE - 1) * FeePerByteAverage + block.FeePerByte) /
        ORDER_AVERAGEING_FEEPERBYTE;

      // Archive Block

      //if (block.Header.Height % INTERVAL_BLOCKHEIGHT_IMAGE == 0)
        // CreateImage();
    }


    protected List<Token> TokenListening = new();

    public void AddTokenListening(Token token)
    {
      TokenListening.Add(token);
    }

    public virtual void DetectAnchorTokenInBlock(TX tX)
    { throw new NotImplementedException(); }

    public virtual void SignalCompletionBlockInsertion(byte[] hash)
    { throw new NotImplementedException(); }

    public virtual void RevokeBlockInsertion()
    { throw new NotImplementedException(); }

    protected abstract void InsertInDatabase(
      Block block, 
      Network.Peer peer);

    public abstract Header ParseHeader(
      byte[] buffer,
      ref int index);

    public string GetName()
    {
      return GetType().Name;
    }

    public abstract bool TryRequestTX(
      byte[] hash,
      out byte[] tXRaw);
  }
}
