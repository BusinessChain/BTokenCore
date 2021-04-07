using System;
using System.Security.Cryptography;



namespace BTokenCore.Chaining
{
  public class HeaderBCash
  {
    public HeaderBCash HeaderPrevious;
    public HeaderBCash HeaderNext;
    
    public const int COUNT_HEADER_BYTES = 68;

    public byte[] Hash;
    public byte[] HashPrevious;
    public byte[] MerkleRoot;
    public uint UnixTimeSeconds;

    public Header HeaderAnchor;



    public HeaderBCash(
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

    public byte[] GetBytes()
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
