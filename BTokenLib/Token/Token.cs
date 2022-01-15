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
        Path.Combine( pathBlockArchive, GetName()));

      Network = new Network(this, Blockchain);
      Miner = new Miner(Blockchain, Network);
    }


    public void Start()
    {
      Console.WriteLine("Load image.");
      Blockchain.LoadImage();

      Console.WriteLine("Start network.");
      Network.Start();
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
      ValidateHeader(block.Header);
      InsertInDatabase(block);

      SignalBlockInsertion.SetResult(block);
    }

    public async Task<Block> AwaitNextBlock()
    {
      SignalBlockInsertion = new();
      return await SignalBlockInsertion.Task;
    }

    protected abstract void InsertInDatabase(Block block);

    public abstract string GetStatus();

    public abstract void ValidateHeader(Header header);

    public abstract Header ParseHeader(
      byte[] buffer,
      ref int index,
      SHA256 sHA256);

    public string GetName()
    {
      return GetType().Name;
    }

    public abstract bool TryRequestTX(
      byte[] hash,
      out byte[] tXRaw);
  }
}
