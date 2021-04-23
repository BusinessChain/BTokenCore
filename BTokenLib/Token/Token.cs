using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public abstract partial class Token
  {
    protected Network Network;
    protected Blockchain Blockchain;

    //protected Miner Miner;


    public async Task Start()
    {
      await Blockchain.LoadImage();
      Network.Start();
      //Miner.Start();
    }

    public abstract Header GetHeaderGenesis();
    public abstract Dictionary<int, byte[]> GetCheckpoints();

    public abstract void LoadImage(string pathImage);
    public abstract void CreateImage(string pathImage);
    public abstract void Reset();

    public abstract void InsertBlock(
      Block block,
      int indexBlockArchive);

    public abstract string GetStatus();

    public abstract void ValidateHeader(
      Header header,
      int height);

    public abstract IParser CreateParser();

    public abstract Header ParseHeader(
      byte[] buffer,
      ref int index);

    public abstract string GetName();

    public abstract bool TryRequestTX(
      byte[] hash,
      out byte[] tXRaw);
  }
}
