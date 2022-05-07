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
    public int CountBlockBytes;

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
          $"{HashPrevious.ToHexString()} but attempts to append {headerPrevious}.");

      HeaderPrevious = headerPrevious;

      Height = headerPrevious.Height + 1;

      DifficultyAccumulated = 
        headerPrevious.DifficultyAccumulated + Difficulty;
    }

    public virtual void AppendToHeader(
      Header headerPrevious,
      byte[] merkleRoot,
      SHA256 sHA256)
    {
      MerkleRoot = merkleRoot;
      Height = headerPrevious.Height + 1;
      headerPrevious.Hash.CopyTo(HashPrevious, 0);
      UnixTimeSeconds = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      Buffer = GetBytes();

      ComputeHash(sHA256);
    }

    void ComputeHash(SHA256 sHA256)
    {
      Hash = sHA256.ComputeHash(sHA256.ComputeHash(Buffer));
    }

    public void IncrementNonce(
      long nonceSeed, 
      SHA256 sHA256)
    {
      Nonce += 1;

      if (Nonce == 0)
        Nonce = (uint)nonceSeed;

      // Buffer need to be updated

      ComputeHash(sHA256);
    }



    public override string ToString()
    {
      return Hash.ToHexString();
    }
  }
}
