using System;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;

namespace BTokenLib
{
  public abstract partial class Token
  {
    public Network Network;
    public Blockchain Blockchain;

    public Miner Miner;
    
    public Token(string pathBlockArchive)
    {
      Blockchain = new Blockchain(
        this,
        pathRootSystem: GetType().Name,
        pathBlockArchive: Path.Combine(pathBlockArchive, GetName()));

      Network = new Network(this, Blockchain);
      Miner = new Miner(Blockchain, Network);
    }


    public virtual void Start()
    {
      Console.WriteLine($"Load image {GetType().Name}.");
      Blockchain.LoadImage();

      Console.WriteLine($"Start network {GetType().Name}.");
      Network.Start();
    }

    public bool IsSynchronized()
    {
      return Network.IsSynchronized();
    }

    public abstract void StartMiner();
    public abstract void StopMiner();

    public abstract Header CreateHeaderGenesis();

    public abstract void LoadImage(string pathImage);
    public abstract void CreateImage(string pathImage);
    public abstract void Reset();

    public abstract Block CreateBlock();
    public abstract Block CreateBlock(int sizeBlockBuffer);


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
  }
}
