using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
      Blockchain = new Blockchain(this, pathBlockArchive);
      Network = new Network(this, Blockchain);
      Miner = new Miner(Blockchain, Network);
    }


    public async Task Start()
    {
      Console.WriteLine("Load image.");
      Blockchain.LoadImage();

      Console.WriteLine("Start network.");
      Network.Start();
    }



    public abstract void StartMiner();

    protected bool FlagMinerStop;
    public void StopMiner()
    {
      FlagMinerStop = true;
    }

    public abstract Header CreateHeaderGenesis();
    public abstract Dictionary<int, byte[]> GetCheckpoints();

    public abstract void LoadImage(string pathImage);
    public abstract void CreateImage(string pathImage);
    public abstract void Reset();

    public abstract Block CreateBlock();
    public abstract Block CreateBlock(int sizeBlockBuffer);

    public abstract void InsertBlock(
      Block block,
      int indexBlockArchive);

    public abstract string GetStatus();

    public abstract void ValidateHeader(Header header);

    public abstract Header ParseHeader(
      byte[] buffer,
      ref int index,
      SHA256 sHA256);

    public abstract string GetName();

    public abstract bool TryRequestTX(
      byte[] hash,
      out byte[] tXRaw);
  }
}
