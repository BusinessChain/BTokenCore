using System.Security.Cryptography;


namespace BTokenCore;

public abstract class Header
{
  internal byte[] Hash;
  internal byte[] HashPrevious;
  internal byte[] MerkleRoot;
  internal uint Nonce;

  internal Header HeaderPrevious;
  internal Header HeaderNext;

  internal Header HeaderParent; // wo wird in die Childs geschrieben?
  internal Dictionary<byte[], byte[]> HashesChild = new(new EqualityComparerByteArray());

  internal int Height;
  internal int CountTXs;

  internal double Difficulty;

  internal long BlockRewardInitial;
  internal int PeriodHalveningBlockReward;

  internal long Fee;


  internal Header()
  {
    Hash = new byte[32];
    HashPrevious = new byte[32];
    MerkleRoot = new byte[32];
  }

  internal Header(
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

  internal abstract byte[] Serialize();

  internal virtual Header AppendToHeader(Header headerPrevious)
  {
    if (!HashPrevious.IsAllBytesEqual(headerPrevious.Hash))
      throw new ProtocolException($"Header {this} references header previous {HashPrevious.ToHexString()} but attempts to append to {headerPrevious}.");

    Height = headerPrevious.Height + 1;
    HeaderPrevious = headerPrevious;

    if (HeaderNext != null)
      return HeaderNext.AppendToHeader(this);
    else
      return this;
  }

  internal virtual void VerifyCoinbase(long valueOutputsTXCoinbase) { }

  internal void ComputeHash()
  {
    SHA256 sHA256 = SHA256.Create();
    ComputeHash(sHA256);
  }

  internal void ComputeHash(SHA256 sHA256)
  {
    Hash = sHA256.ComputeHash(
      sHA256.ComputeHash(Serialize()));
  }
}
