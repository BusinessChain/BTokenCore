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
    public Token TokenChild;
    public Token TokenParent;

    public Blockchain Blockchain;

    List<string> AddressPool = new();


    public Token(
      string pathBlockArchive)
    {
      Blockchain = new Blockchain(
        this,
        pathRootSystem: GetType().Name,
        pathBlockArchive: Path.Combine(pathBlockArchive, GetName()));

      string pathAddressPoolPeers = Path.Combine(
        GetType().Name, 
        "AddressPoolPeers");

      if (File.Exists(pathAddressPoolPeers))
        AddressPool = File.ReadAllLines(pathAddressPoolPeers).ToList();
      else
        AddressPool = new();
    }


    public virtual void LoadImage()
    {
      Debug.WriteLine($"Load image {GetType().Name}.");
      Blockchain.LoadImage();
    }

    public Token GetParentRoot()
    {
      Token tokenRoot = this;

      while (tokenRoot.TokenParent != null)
        tokenRoot = tokenRoot.TokenParent;

      return tokenRoot;
    }

    public abstract Header CreateHeaderGenesis();

    public abstract void LoadImage(string pathImage);
    public abstract void CreateImage(string pathImage);
    public abstract void Reset();

    public abstract Block CreateBlock();
    public abstract Block CreateBlock(int sizeBlockBuffer);


    protected bool IsMining;
    protected bool FlagMiningCancel;

    public abstract void StartMining(object network);
    public void StopMining()
    {
      if (IsMining)
        FlagMiningCancel = true;
    }

    protected async Task RunMining(Network network)
    {
      await RunMining(network, 0);
    }
    protected async Task RunMining(Network network, long seed)
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
          Blockchain.InsertBlock(block);

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

        network.RelayBlock(block);
      }

    LABEL_Exit_Miner:

      Console.WriteLine($"{GetType().Name} miner " +
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

    public abstract void LoadTXs(Block block);

    public abstract byte[] SendDataTX(string data);
    public abstract byte[] SendTokenTX(string data);

    protected abstract void InsertInDatabase(Block block);

    public abstract string GetStatus();

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
      List<string> listExclusion)
    {
      if (AddressPool.Count <= countMax)
        return AddressPool.ToList();

      List<string> iPAddresses = new();
      List<string> iPAddressesTemporaryRemovedFromPool = new();

      while (
        iPAddresses.Count < countMax && 
        AddressPool.Count > 0)
      {
        int randomIndex = RandomGenerator
          .Next(AddressPool.Count);

        string iPAddress = AddressPool[randomIndex];

        AddressPool.RemoveAt(randomIndex);
        iPAddressesTemporaryRemovedFromPool.Add(iPAddress);

        if (!listExclusion.Contains(iPAddress))
          iPAddresses.Add(iPAddress);
      }

      AddressPool.AddRange(iPAddressesTemporaryRemovedFromPool);

      return iPAddresses;
    }

    public bool DoesPeerSupportProtocol(string iPAddressPeer)
    {
      return AddressPool.Contains(iPAddressPeer);
    }
  }
}
