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

    List<string> IPAddressPool = new();

    protected List<TX> TXPool = new();


    public Token()
    {
      Blockchain = new Blockchain(this);

      Network = new(this);

      Wallet = new();

      string pathAddressPoolPeers = Path.Combine(
        GetName(), 
        "AddressPoolPeers");

      try
      {
        IPAddressPool = File.ReadAllLines(pathAddressPoolPeers).ToList();
      }
      catch
      {
        IPAddressPool = new();
        IPAddressPool.Add("3.67.200.137");
      }
    }

    public void Start()
    {
      if (TokenParent != null)
        TokenParent.Start();
      
      Blockchain.LoadImage();
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


    object LOCK_IsLocked = new();
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
      lock (LOCK_IsLocked)
      {
        IsLocked = false;
      }
    }


    public Token GetParentRoot()
    {
      if (TokenParent == null)
        return this;

      return TokenParent.GetParentRoot();
    }

    public abstract Header CreateHeaderGenesis();

    public abstract void LoadImage(string pathImage);
    public abstract void CreateImage(string pathImage);
    public abstract void Reset();

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
          Blockchain.InsertBlock(
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

    public void InsertBlock(Block block)
    {
      InsertInDatabase(block);

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



    Random RandomGenerator = new();

    public List<string> RetrieveIPAdresses(
      int countMax,
      List<string> iPAddressesExclusion)
    {
      List<string> iPAddresses = new();
      List<string> iPAddressesTemporaryRemovedFromPool = new();

      iPAddressesExclusion.ForEach(i => {
        if(IPAddressPool.Contains(i))
        {
          IPAddressPool.Remove(i);
          iPAddressesTemporaryRemovedFromPool.Add(i);
        }
      });

      while (
        iPAddresses.Count < countMax && 
        IPAddressPool.Count > 0)
      {
        int randomIndex = RandomGenerator
          .Next(IPAddressPool.Count);

        string iPAddress = IPAddressPool[randomIndex];

        IPAddressPool.RemoveAt(randomIndex);
        iPAddressesTemporaryRemovedFromPool.Add(iPAddress);

        if (!iPAddressesExclusion.Contains(iPAddress))
          iPAddresses.Add(iPAddress);
      }

      IPAddressPool.AddRange(iPAddressesTemporaryRemovedFromPool);

      return iPAddresses;
    }

    public bool DoesPeerSupportProtocol(string iPAddressPeer)
    {
      return IPAddressPool.Contains(iPAddressPeer);
    }
  }
}
