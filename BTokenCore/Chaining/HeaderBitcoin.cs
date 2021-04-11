using System;
using System.Security.Cryptography;



namespace BTokenCore.Chaining
{
  class HeaderBitcoin : Blockchain.Header
  {
    const int COUNT_HEADER_BYTES = 80;

    public uint Version;
    public uint NBits;
    public uint Nonce;

    const double MAX_TARGET = 2.695994666715064E67;



    public HeaderBitcoin(
      byte[] headerHash,
      uint version,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds,
      uint nBits,
      uint nonce) : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        unixTimeSeconds)
    {
      Version = version;
      NBits = nBits;
      Nonce = nonce;

      Difficulty = MAX_TARGET /
        (double)UInt256.ParseFromCompact(nBits);
    }

    public override byte[] GetBytes()
    {
      byte[] headerSerialized = 
        new byte[COUNT_HEADER_BYTES];

      BitConverter.GetBytes(Version)
        .CopyTo(headerSerialized, 0);

      HashPrevious.CopyTo(headerSerialized, 4);

      MerkleRoot.CopyTo(headerSerialized, 36);

      BitConverter.GetBytes(UnixTimeSeconds)
        .CopyTo(headerSerialized, 68);

      BitConverter.GetBytes(NBits)
        .CopyTo(headerSerialized, 72);

      BitConverter.GetBytes(Nonce)
        .CopyTo(headerSerialized, 76);

      return headerSerialized;
    }
  }
}
