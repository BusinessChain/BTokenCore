namespace BTokenCore;

public class HeaderBToken : Header
{
  internal const int COUNT_HEADER_BYTES = 104;

  const long BLOCK_REWARD_INITIAL = 20000000000; // 200 BTK
  const int PERIOD_HALVENING_BLOCK_REWARD = 52500;

  internal byte[] HashDatabase = new byte[32];


  internal HeaderBToken()
  {
    Difficulty = 1;
  }

  internal HeaderBToken(
    byte[] headerHash,
    byte[] hashPrevious,
    byte[] merkleRootHash,
    byte[] hashDatabase,
    uint nonce)
    : this()
  {
    Hash = headerHash;
    HashPrevious = hashPrevious;
    MerkleRoot = merkleRootHash;
    HashDatabase = hashDatabase;
    Nonce = nonce;
  }

  internal HeaderBToken(byte[] buffer, ref int index)
    : base(buffer, ref index, COUNT_HEADER_BYTES)
  {
    Difficulty = 1;

    HashDatabase = buffer.ReadBytes(ref index, 32);

    Nonce = buffer.Read<uint>(ref index);
  }

  internal override byte[] Serialize()
  {
    byte[] buffer = SerializePrefix(COUNT_HEADER_BYTES);

    HashDatabase.CopyTo(buffer, 68);

    BitConverter.GetBytes(Nonce).CopyTo(buffer, 100);

    return buffer;
  }

  internal override void CopyTo(Header header)
  {
    HeaderBToken headerBToken = (HeaderBToken)header;

    headerBToken.MerkleRoot = MerkleRoot;
    headerBToken.Nonce = Nonce;
    headerBToken.Version = Version;
    headerBToken.HashDatabase = HashDatabase;
  }

  internal static long GetBlockReward(int height)
  {
    return BLOCK_REWARD_INITIAL >> height / PERIOD_HALVENING_BLOCK_REWARD;
  }

  internal override void VerifyCoinbase(long valueOutputsTXCoinbase)
  {
    if (GetBlockReward(Height) + Fee != valueOutputsTXCoinbase)
      throw new ProtocolException($"Output values of coinbase not equal to blockReward plus tx fees.");
  }
}