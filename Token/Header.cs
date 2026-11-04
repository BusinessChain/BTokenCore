namespace BTokenCore;

public abstract class Header
{
  internal byte[] Hash;
  internal uint Version;
  internal byte[] HashPrevious;
  internal byte[] MerkleRoot;
  internal uint Nonce;

  internal Header HeaderPrevious;
  internal Header HeaderNext;

  internal Header HeaderParent;
  internal Dictionary<byte[], TXOutputTokenAnchor> AnchorsWinner = new(new EqualityComparerByteArray());

  internal int Height;
  internal int CountTXs;

  internal double Difficulty;


  internal Header()
  {
  }

  protected Header(byte[] buffer, ref int index, int countHeaderBytes)
  {
    Hash = Crypto.ComputeHash256(buffer.AsSpan(index, countHeaderBytes));

    Version = buffer.Read<uint>(ref index);

    HashPrevious = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH256);

    MerkleRoot = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH256);
  }

  internal abstract byte[] Serialize();

  protected byte[] SerializePrefix(int countHeaderBytes)
  {
    byte[] buffer = new byte[countHeaderBytes];

    BitConverter.GetBytes(Version).CopyTo(buffer, 0);

    HashPrevious.CopyTo(buffer, 4);

    MerkleRoot.CopyTo(buffer, 36);

    return buffer;
  }

  internal virtual void AppendToHeader(Header headerPrevious)
  {
    if (!HashPrevious.IsAllBytesEqual(headerPrevious.Hash))
      throw new ProtocolException($"Header {this} references header previous {HashPrevious.ToHexString()} but attempts to append to {headerPrevious}.");

    Height = headerPrevious.Height + 1;
    HeaderPrevious = headerPrevious;
  }

  internal bool IsParentNewest()
  {
    return HeaderParent != null && HeaderParent.HeaderNext == null;
  }

  internal Header GetHeaderAncestor(int height)
  {
    Header header = this;

    while (header != null && header.Height > height)
      header = header.HeaderPrevious;

    return header;
  }

  internal virtual void CopyTo(Header header) { }

  internal virtual void VerifyCoinbase(List<TX> tXs) { }

  internal void ComputeHash()
  {
    Hash = Crypto.ComputeHash256(Serialize());
  }
}
