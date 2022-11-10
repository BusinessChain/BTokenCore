﻿using System;
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

    public Blockchain Blockchain;

    BlockArchiver Archiver;

    public Wallet Wallet;

    public Network Network;
    public UInt16 Port;

    protected List<TX> TXPool = new();

    protected StreamWriter LogFile;



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


    public Token(
      UInt16 port, 
      bool flagEnableInboundConnections)
    {
      PathRootToken = GetName();
      Directory.CreateDirectory(PathRootToken);

      Blockchain = new Blockchain(this);

      Archiver = new(this, GetName());

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
      try
      {
        block.Header.AppendToHeader(Blockchain.HeaderTip);

        InsertInDatabase(block);

        Blockchain.AppendHeader(block.Header);

        FeePerByteAverage =
          ((ORDER_AVERAGEING_FEEPERBYTE - 1) * FeePerByteAverage + block.FeePerByte) /
          ORDER_AVERAGEING_FEEPERBYTE;

        // Archiver.ArchiveBlock(block);

        if (block.Header.Height % INTERVAL_BLOCKHEIGHT_IMAGE == 0)
          CreateImage();

        TokenListening.ForEach(
          t => t.SignalCompletionBlockInsertion(block.Header.Hash));
      }
      catch (ProtocolException ex)
      {
        // Database (wallet) recovery.

        TokenListening.ForEach(t => t.RevokeBlockInsertion());
        throw ex;
      }
    }

    public virtual Block GetBlock(byte[] hash)
    { throw new NotImplementedException(); }

    public virtual void InsertDB(
      byte[] bufferDB,
      int lengthDataInBuffer)
    { throw new NotImplementedException(); }

    public virtual void DeleteDB()
    { throw new NotImplementedException(); }

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

    public virtual bool FlagDownloadDBWhenSync(HeaderDownload headerDownload)
    {
      return false;
    }

    public void BroadcastTX(TX tX)
    {
      TXPool.Add(tX);
      Network.AdvertizeTX(tX);
    }
  }
}
