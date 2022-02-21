using System;
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


    static string NameFork = "Fork";
    static string NameImage = "Image";
    static string NameImageOld = "ImageOld";

    string PathImage;
    string PathImageOld;
    string PathImageFork;
    string PathImageForkOld;

    string PathTokenRoot;


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
      
      LoadImage();
      Network.Start();
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
        lock (this)
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
      TryMoveDirectory(PathImageFork, PathImage);
      TryMoveDirectory(PathImageForkOld, PathImageOld);
    }

    static bool TryMoveDirectory(string pathSource, string pathDest)
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

    public void LoadImage()
    {
      LoadImage(0);
    }

    public void LoadImage(int heightMax)
    {
      string pathImageLoad = PathImage;

      while (true)
      {
        Reset();

        if (heightMax == 0)
          return;

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
        TryMoveDirectory(pathImage, PathImageForkOld);
      }
      else
      {
        pathImage = PathImage;
        TryMoveDirectory(pathImage, PathImageOld);
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
    public abstract Block CreateBlock(int sizeBlockBuffer);


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
          InsertBlock(
            block, 
            flagCreateImage: true);

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

      Console.WriteLine($"{GetName()} miner " +
        $"on thread {Thread.CurrentThread.ManagedThreadId} canceled.");
    }

    protected abstract Task<Block> MineBlock(SHA256 sHA256, long seed); 


    TaskCompletionSource<Block> SignalBlockInsertion;

    public void InsertBlock(Block block, bool flagCreateImage)
    {
      Blockchain.InsertHeader(block.Header);
      InsertInDatabase(block);

      if (flagCreateImage)
        CreateImage();

      SignalBlockInsertion.SetResult(block);
    }

    public async Task<Block> AwaitNextBlock()
    {
      SignalBlockInsertion = new();
      return await SignalBlockInsertion.Task;
    }

    public byte[] SendDataTX(byte[] data)
    {
      TX tXAnchorToken = CreateDataTX(data);

      TXPool.Add(tXAnchorToken);

      Network.AdvertizeToken(tXAnchorToken.Hash);

      return tXAnchorToken.Hash;
    }

    public abstract TX CreateDataTX(byte[] data);

    protected abstract void InsertInDatabase(Block block);


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
