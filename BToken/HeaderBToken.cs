namespace BTokenCore;

public class HeaderBToken : Header
{
  internal const int COUNT_HEADER_BYTES = 104;

  const long BLOCK_REWARD_INITIAL = 20_000_000_000;
  const int PERIOD_HALVENING_BLOCK_REWARD = 52500;
  internal const long SUPPLY_MAX = 2 * BLOCK_REWARD_INITIAL * PERIOD_HALVENING_BLOCK_REWARD;

  internal byte[] HashDatabase;


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

    HashDatabase = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH256);

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
    int countHalvings = height / PERIOD_HALVENING_BLOCK_REWARD;

    if (countHalvings >= 64)
      return 0;

    return BLOCK_REWARD_INITIAL >> countHalvings;
  }

  internal override void VerifyCoinbase(List<TX> tXs)
  {
    long fee = 0;

    for (int i = 1; i < tXs.Count; i += 1)
    {
      if (tXs[i].Fee > SUPPLY_MAX - fee)
        throw new ProtocolException($"Fees of block exceed supply max.");

      fee += tXs[i].Fee;
    }

    if (GetBlockReward(Height) + fee != tXs[0].GetValueOutputs())
      throw new ProtocolException($"Output values of coinbase not equal to blockReward plus tx fees.");
  }
}