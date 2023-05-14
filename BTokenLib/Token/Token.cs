﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public Token TokenParent;
    protected List<Token> TokenChilds = new();

    public Blockchain Blockchain;

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

      Blockchain = new Blockchain(this);

      Archiver = new(GetName());

      TXPool = new();

      Port = port;
      Network = new(this, flagEnableInboundConnections);

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

      LogFile = new StreamWriter(
        Path.Combine(GetName(), "LogToken"),
        false);
    }

    public void Start()
    {
      if (TokenParent != null)
        TokenParent.Start();

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

    public virtual void CreateImageDatabase(string path) 
    { }

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

        Block block = CreateBlock();
        int heightBlock = Blockchain.HeaderTip.Height + 1;

        while (Archiver.TryLoadBlockArchive(heightBlock++, out byte[] buffer))
        {
          block.Buffer = buffer;
          block.Parse();

          try
          {
            block.Header.AppendToHeader(Blockchain.HeaderTip);
            InsertInDatabase(block);
            Blockchain.AppendHeader(block.Header);
          }
          catch
          {
            break;
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
      block.Header.AppendToHeader(Blockchain.HeaderTip);

      InsertInDatabase(block);

      TXPool.RemoveTXs(block.TXs.Select(tX => tX.Hash));

      Blockchain.AppendHeader(block.Header);

      FeePerByteAverage =
        ((ORDER_AVERAGEING_FEEPERBYTE - 1) * FeePerByteAverage + block.FeePerByte) /
        ORDER_AVERAGEING_FEEPERBYTE;

      Archiver.ArchiveBlock(block);

      if (block.Header.Height % INTERVAL_BLOCKHEIGHT_IMAGE == 0)
        CreateImage();

      TokenChilds.ForEach(
        t => t.SignalCompletionBlockInsertion(block.Header));
    }

    public virtual Block GetBlock(byte[] hash)
    {
      if(Blockchain.TryGetHeader(hash, out Header header))
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

    public void AddTokenListening(Token token)
    {
      TokenChilds.Add(token);
    }

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
      Network.AdvertizeTX(tX);
    }
  }
}
