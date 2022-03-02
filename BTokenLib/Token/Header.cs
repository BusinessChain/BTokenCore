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
      uint unixTimeSeconds)
    {
      Hash = headerHash;
      HashPrevious = hashPrevious;
      MerkleRoot = merkleRootHash;
      UnixTimeSeconds = unixTimeSeconds;
    }

    public abstract byte[] GetBytes();

    public virtual void AppendToHeader(Header headerPrevious)
    {
      if (!HashPrevious.IsEqual(headerPrevious.Hash))
        throw new InvalidOperationException(
          $"Header {this} references header previous " +
          $"{HashPrevious.ToHexString()} but attempts to append {headerPrevious}.");

      HeaderPrevious = headerPrevious;

      Height = headerPrevious.Height + 1;

      DifficultyAccumulated = 
        headerPrevious.DifficultyAccumulated + Difficulty;

      Validate();
    }

    public abstract void Validate();

    public virtual void CreateAppendingHeader(
      SHA256 sHA256,
      byte[] merkleRoot,
      Header headerTip)
    {
      MerkleRoot = merkleRoot;
      Height = headerTip.Height + 1;
      headerTip.Hash.CopyTo(HashPrevious, 0);
      UnixTimeSeconds = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      Buffer = GetBytes();

      Hash = sHA256.ComputeHash(sHA256.ComputeHash(Buffer));
    }



    public override string ToString()
    {
      return Hash.ToHexString();
    }
  }
}
