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
      await Blockchain.LoadImage();

      Console.WriteLine("Start network.");
      Network.Start();
    }

    CancellationTokenSource CancellationMiner = new();

    public async Task StartMiner()
    {
    StartMiner:

      while (!Blockchain.TryLock())
      {
        await Task.Delay(1000).ConfigureAwait(false);
      }

      Header headerNew = CreateHeaderNew(
        Blockchain.HeaderTip);

      Blockchain.ReleaseLock();

      Block blockNew;

      try
      {
        blockNew = await MineBlockNew(
          headerNew,
          CancellationMiner.Token);
      }
      catch(TaskCanceledException)
      {
        // maybe return TXs to pool?

        CancellationMiner = new();
        goto StartMiner;
      }

      while (!Blockchain.TryLock())
      {
        await Task.Delay(200).ConfigureAwait(false);
      }

      try
      {
        Blockchain.InsertBlock(
          blockNew,
          flagValidateHeader: true);

        //Blockchain.ArchiveBlock(
        //    blockNew,
        //    UTXOIMAGE_INTERVAL_SYNC);
      }
      catch(Exception ex)
      {
        Debug.WriteLine(
          $"{ex.GetType().Name} when inserting " +
          $"mined block {blockNew.Header.Hash.ToHexString()}");
      }

      Blockchain.ReleaseLock();
    }

    public void UpdateMiner(Header header)
    {

    }

    // Bei PoW wenn Hash gefunden,
    // bei dPoW wenn bestätigt in der Carrier chain.
    public abstract Task<Block> MineBlockNew(
      Header header,
      CancellationToken cancellationToken);
    public abstract Header CreateHeaderNew(Header header);

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

    public void ValidateHeaders(Header header)
    {
      do
      {
        ValidateHeader(header);
        header = header.HeaderNext;
      } while (header != null);
    }

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
