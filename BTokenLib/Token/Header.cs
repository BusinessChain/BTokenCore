using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;



namespace BTokenLib
{
  public abstract class Header
  {
    public byte[] Hash;
    public byte[] HashPrevious;
    public byte[] MerkleRoot;
    public uint Nonce;

    public Header HeaderPrevious;
    public Header HeaderNext;

    public Header HeaderParent;
    public Dictionary<byte[], byte[]> HashesChild = new(new EqualityComparerByteArray());

    public int Height;
    public int CountTXs;

    public double Difficulty;
    public double DifficultyAccumulated;

    public long BlockRewardInitial;
    public int PeriodHalveningBlockReward;

    public long Fee;


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
      uint nonce)
    {
      Hash = headerHash;
      HashPrevious = hashPrevious;
      MerkleRoot = merkleRootHash;
      Nonce = nonce;
    }

    public abstract byte[] Serialize();

    public virtual Header AppendToHeader(Header headerPrevious)
    {
      if (!HashPrevious.IsAllBytesEqual(headerPrevious.Hash))
        throw new ProtocolException($"Header {this} references header previous {HashPrevious.ToHexString()} but attempts to append to {headerPrevious}.");

      Height = headerPrevious.Height + 1;
      HeaderPrevious = headerPrevious;
      DifficultyAccumulated = headerPrevious.DifficultyAccumulated + Difficulty;

      if (HeaderNext != null)
        return HeaderNext.AppendToHeader(this);
      else
        return this;
    }

    public virtual void VerifyCoinbase(long valueOutputsTXCoinbase) { }

    public void ComputeHash()
    {
      SHA256 sHA256 = SHA256.Create();
      ComputeHash(sHA256);
    }

    public void ComputeHash(SHA256 sHA256)
    {
      Hash = sHA256.ComputeHash(
        sHA256.ComputeHash(Serialize()));
    }

    public override string ToString()
    {
      return Hash.ToHexString();
    }
  }
}
