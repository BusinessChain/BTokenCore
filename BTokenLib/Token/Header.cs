using System;



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
    //{
    //  var headerSerialized = new byte[COUNT_HEADER_BYTES];

    //  HashPrevious.CopyTo(headerSerialized, 0);

    //  MerkleRoot.CopyTo(headerSerialized, 32);

    //  BitConverter.GetBytes(UnixTimeSeconds)
    //    .CopyTo(headerSerialized, 64);

    //  return headerSerialized;
    //}

    public virtual void AppendToHeader(Header headerPrevious)
    {
      if (!HashPrevious.IsEqual(headerPrevious.Hash))
        throw new ProtocolException(
          $"Wrong header previous when appending to headerchain.");

      HeaderPrevious = headerPrevious;

      Height = headerPrevious.Height + 1;

      ValidateHeader();
    }

    public abstract void ValidateHeader();

    public override string ToString()
    {
      return Hash.ToHexString();
    }
  }
}
