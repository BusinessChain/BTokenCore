namespace BTokenCore;

public class HeaderBToken : Header
{
  internal const int COUNT_HEADER_BYTES = 104;

  internal byte[] HashAccounts;


  internal HeaderBToken()
  {
    Difficulty = 1;
  }

  internal HeaderBToken(
    byte[] headerHash,
    byte[] hashPrevious,
    byte[] merkleRootHash,
    byte[] hashAccounts,
    uint nonce)
    : this()
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
    Difficulty = 1;

    HashAccounts = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH256);

    Nonce = buffer.Read<uint>(ref index);
  }

  internal override byte[] Serialize()
  {
    byte[] buffer = SerializePrefix(COUNT_HEADER_BYTES);

    HashAccounts.CopyTo(buffer, 68);

    BitConverter.GetBytes(Nonce).CopyTo(buffer, 100);

    return buffer;
  }

  internal override void CopyTo(Header header)
  {
    HeaderBToken headerBToken = (HeaderBToken)header;

    headerBToken.MerkleRoot = MerkleRoot;
    headerBToken.Nonce = Nonce;
    headerBToken.Version = Version;
    headerBToken.HashAccounts = HashAccounts;
  }
}