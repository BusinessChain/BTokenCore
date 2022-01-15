using System;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBitcoin : Header
  {
    public const int COUNT_HEADER_BYTES = 80;

    public uint Version;
    public uint NBits;
    public uint Nonce;

    const double MAX_TARGET = 2.695994666715064E67;


    public HeaderBitcoin()
    {
      Buffer = new byte[COUNT_HEADER_BYTES];
    }

    public HeaderBitcoin(
      byte[] headerHash,
      uint version,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds,
      uint nBits,
      uint nonce ) 
      : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        unixTimeSeconds)
    {
      Version = version;
      NBits = nBits;
      Nonce = nonce;

      ComputeDifficultyFromNBits();
    }

    public void ComputeDifficultyFromNBits()
    {
      Difficulty = MAX_TARGET /
        (double)UInt256.ParseFromCompact(NBits);
    }

    public void IncrementNonce()
    {
      Nonce += 1;
      Buffer.Increment(76, 4);
    }

    public override byte[] GetBytes()
    {
      BitConverter.GetBytes(Version)
        .CopyTo(Buffer, 0);

      HashPrevious.CopyTo(Buffer, 4);

      MerkleRoot.CopyTo(Buffer, 36);

      BitConverter.GetBytes(UnixTimeSeconds)
        .CopyTo(Buffer, 68);

      BitConverter.GetBytes(NBits)
        .CopyTo(Buffer, 72);

      BitConverter.GetBytes(Nonce)
        .CopyTo(Buffer, 76);

      return Buffer;
    }
  }
}
