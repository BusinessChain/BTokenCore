using System;



namespace BTokenLib
{
  public class Header
  {
    const int COUNT_HEADER_BYTES = 68;

    public byte[] Hash;
    public byte[] HashPrevious;
    public byte[] MerkleRoot;
    public uint UnixTimeSeconds;

    public double Difficulty;
    public double DifficultyAccumulated;

    public Header HeaderPrevious;
    public Header HeaderNext;

    public int Height;

    public int IndexBlockArchive;
    public int StartIndexBufferArchive;
    public int StopIndexBufferArchive;


    public Header()
    {
      Hash = new byte[32];
      HashPrevious = new byte[32];
      MerkleRoot = new byte[32];
    }

    public Header(
      byte[] headerHash,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds)
    {
      Hash = headerHash;
      HashPrevious = hashPrevious;
      MerkleRoot = merkleRootHash;
      UnixTimeSeconds = unixTimeSeconds;
    }

    public virtual byte[] GetBytes()
    {
      var headerSerialized = new byte[COUNT_HEADER_BYTES];

      HashPrevious.CopyTo(headerSerialized, 0);

      MerkleRoot.CopyTo(headerSerialized, 32);

      BitConverter.GetBytes(UnixTimeSeconds)
        .CopyTo(headerSerialized, 64);

      return headerSerialized;
    }

    public void ExtendHeaderTip(ref Header headerTip)
    {
      HeaderPrevious = headerTip;

      Height = headerTip.Height + 1;
      DifficultyAccumulated =
        headerTip.DifficultyAccumulated + Difficulty;

      headerTip.HeaderNext = this;
      headerTip = this;
    }

    public override string ToString()
    {
      return Hash.ToHexString();
    }
  }
}
