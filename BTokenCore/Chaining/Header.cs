using System;
using System.Security.Cryptography;



namespace BTokenCore.Chaining
{
  public class Header
  {
    const int COUNT_HEADER_BYTES = 68;

    public Header HeaderPrevious;
    public Header HeaderNext;

    public byte[] Hash;
    public byte[] HashPrevious;
    public byte[] MerkleRoot;
    public uint UnixTimeSeconds;
    public double Difficulty;



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
  }
}
