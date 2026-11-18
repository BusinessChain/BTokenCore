using System.Numerics;

namespace BTokenCore;

public class HeaderBToken : Header
{
  internal const int COUNT_HEADER_BYTES = 136;

  internal byte[] HashAccounts;
  internal byte[] HashAnchors = Blockchain.ParserToken.ComputeHashAnchors([]);


  internal HeaderBToken()
  { }

  internal HeaderBToken(
    byte[] headerHash,
    byte[] hashPrevious,
    byte[] merkleRootHash,
    byte[] hashAccounts,
    uint nonce)
  {
    Hash = headerHash;
    HashPrevious = hashPrevious;
    MerkleRoot = merkleRootHash;
    HashAccounts = hashAccounts;
    Nonce = nonce;
  }

  internal HeaderBToken(byte[] buffer, ref int index)
    : base(buffer, ref index, COUNT_HEADER_BYTES)
  {
    HashAccounts = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH256);

    HashAnchors = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH256);

    Nonce = buffer.Read<uint>(ref index);
  }

  internal override Dictionary<string, object> GetSnapshotState()
  {
    Dictionary<string, object> snapshotState = base.GetSnapshotState();

    snapshotState["hashAccounts"] = HashAccounts.ToHexString();
    snapshotState["hashAnchors"] = HashAnchors.ToHexString();
    snapshotState["heightParent"] = HeaderParent.Height;

    return snapshotState;
  }

  internal override byte[] Serialize()
  {
    byte[] buffer = SerializePrefix(COUNT_HEADER_BYTES);

    HashAccounts.CopyTo(buffer, 68);

    HashAnchors.CopyTo(buffer, 100);

    BitConverter.GetBytes(Nonce).CopyTo(buffer, 132);

    return buffer;
  }

  protected override BigInteger GetWork()
  {
    return BigInteger.One;
  }

  internal override void CopyTo(Header header)
  {
    HeaderBToken headerBToken = (HeaderBToken)header;

    headerBToken.MerkleRoot = MerkleRoot;
    headerBToken.Nonce = Nonce;
    headerBToken.Version = Version;
    headerBToken.HashAccounts = HashAccounts;
    headerBToken.HashAnchors = HashAnchors;
  }
}