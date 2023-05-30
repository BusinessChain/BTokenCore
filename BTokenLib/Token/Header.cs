using System;
using System.Security.Cryptography;



namespace BTokenLib
{
  public abstract class Header
  {
    public byte[] Buffer;

    public byte[] Hash;
    public byte[] HashPrevious;
    public byte[] MerkleRoot;
    public uint UnixTimeSeconds;
    public uint Nonce;

    public Header HeaderPrevious;
    public Header HeaderNext;

    public int Height;

    public int IndexBlockArchive;
    public int StartIndexBlockArchive;

    public int CountBytesBlock;
    public long CountBytesBlocksAccumulated;

    public double Difficulty;
    public double DifficultyAccumulated;



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
      uint unixTimeSeconds,
      uint nonce)
    {
      Hash = headerHash;
      HashPrevious = hashPrevious;
      MerkleRoot = merkleRootHash;
      UnixTimeSeconds = unixTimeSeconds;
      Nonce = nonce;
    }

    public abstract byte[] GetBytes();

    public virtual void AppendToHeader(Header headerPrevious)
    {
      if (!HashPrevious.IsEqual(headerPrevious.Hash))
        throw new ProtocolException(
          $"Header {this} references header previous " +
          $"{HashPrevious.ToHexString()} but attempts to append to {headerPrevious}.");

      HeaderPrevious = headerPrevious;

      Height = headerPrevious.Height + 1;

      DifficultyAccumulated = headerPrevious.DifficultyAccumulated + Difficulty;
      CountBytesBlocksAccumulated = headerPrevious.CountBytesBlocksAccumulated + CountBytesBlock;
    }

    public void ComputeHash(SHA256 sHA256)
    {
      Hash = sHA256.ComputeHash(sHA256.ComputeHash(GetBytes()));
    }

    public override string ToString()
    {
      return Hash.ToHexString();
    }
  }
}
