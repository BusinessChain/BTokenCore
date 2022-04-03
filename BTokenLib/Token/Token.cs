﻿using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public Token TokenParent;

    public Blockchain Blockchain;

    public Wallet Wallet;

    public Network Network;

    protected List<TX> TXPool = new();

    protected List<ushort> IDsBToken = new();


    static string NameFork = "Fork";
    static string NameImage = "Image";
    static string NameImageOld = "ImageOld";

    string PathImage;
    string PathImageOld;
    string PathImageFork;
    string PathImageForkOld;

    string PathTokenRoot;

    const int INTERVAL_BLOCKHEIGHT_IMAGE = 200;


    public Token()
    {
      Blockchain = new Blockchain(this);

      Network = new(this);

      Wallet = new();

      PathTokenRoot = GetName();
      Directory.CreateDirectory(PathTokenRoot);

      PathImage = Path.Combine(
        PathTokenRoot,
        NameImage);

      PathImageOld = Path.Combine(
        PathTokenRoot,
        NameImageOld);

      PathImageFork = Path.Combine(
        PathTokenRoot,
        NameFork,
        NameImage);

      PathImageForkOld = Path.Combine(
        PathTokenRoot,
        NameFork,
        NameImageOld);
    }

    public void Start()
    {
      if (TokenParent != null)
        TokenParent.Start();
      
      LoadImage(int.MaxValue);
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

    public bool TryLockRoot()
    {
      if (TokenParent != null)
        return TokenParent.TryLockRoot();

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

    public abstract void CreateImageDatabase(string path);

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

    public abstract void LoadImageDatabase(string path);

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

    public void Reset()
    {
      Blockchain.InitializeHeaderchain();
      ResetDatabase(); 
    }

    public abstract void ResetDatabase();

    public abstract Block CreateBlock();

    protected bool IsMining;
    protected bool FlagMiningCancel;

    public abstract void StartMining();
    public void StopMining()
    {
      if (IsMining)
        FlagMiningCancel = true;
    }

    protected async Task RunMining()
    {
      await RunMining(0);
    }
    protected async Task RunMining(long seed)
    {
      SHA256 sHA256 = SHA256.Create();

      while (!FlagMiningCancel)
      {
        Block block = await MineBlock(sHA256, seed);

        while (!Blockchain.TryLock())
        {
          if (FlagMiningCancel)
            goto LABEL_Exit_Miner;

          Console.WriteLine("Miner awaiting access of BToken blockchain LOCK.");
          Thread.Sleep(1000);
        }

        Console.Beep();

        try
        {
          InsertBlock(block);

          Debug.WriteLine($"Mined block {block}.");
        }
        catch (Exception ex)
        {
          Debug.WriteLine(
            $"{ex.GetType().Name} when inserting mined block {block}.");

          continue;
        }
        finally
        {
          Blockchain.ReleaseLock();
        }

        Network.RelayBlock(block);
      }

    LABEL_Exit_Miner:

      Console.WriteLine($"{GetName()} miner on thread " +
        $"{Thread.CurrentThread.ManagedThreadId} canceled.");
    }

    protected abstract Task<Block> MineBlock(
      SHA256 sHA256, 
      long seed);

    public void InsertBlock(Block block)
    {
      block.Header.AppendToHeader(Blockchain.HeaderTip);
      
      if(TryInsertInDatabase(block))
      {
        Blockchain.AppendHeader(block.Header);

        if (block.Header.Height == INTERVAL_BLOCKHEIGHT_IMAGE)
          CreateImage();
      }
    }

    protected List<Token> TokenListening = new();

    public void AddTokenListening(Token token)
    {
      TokenListening.Add(token);
    }

    public void AddIDBToken(ushort iDBToken)
    {
      IDsBToken.Add(iDBToken);
    }


    public virtual void DetectAnchorToken(TXOutput tXOutput)
    {
      throw new NotImplementedException();
    }
    public virtual void SignalBlockInsertion(byte[] hash)
    {
      throw new NotImplementedException();
    }
    public virtual void RevokeBlockInsertion()
    {
      throw new NotImplementedException();
    }

    public byte[] SendDataTX(byte[] data)
    {
      TX tXAnchorToken = CreateDataTX(data);

      TXPool.Add(tXAnchorToken);

      Network.AdvertizeToken(tXAnchorToken.Hash);

      return tXAnchorToken.Hash;
    }

    public abstract TX CreateDataTX(byte[] data);

    protected abstract bool TryInsertInDatabase(Block block);

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
