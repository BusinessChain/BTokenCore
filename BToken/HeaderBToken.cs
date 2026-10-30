using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  public class HeaderBToken : Header
  {
    internal const int COUNT_HEADER_BYTES = 104;

    const long BLOCK_REWARD_INITIAL = 200000000000000; // 200 BTK
    const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

    internal uint Version;
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

    internal HeaderBToken(byte[] buffer, ref int index, SHA256 sHA256)
      : this()
    {
      Hash =
        sHA256.ComputeHash(
          sHA256.ComputeHash(
            buffer,
            index,
            COUNT_HEADER_BYTES));

      Version = BitConverter.ToUInt32(buffer, index);
      index += 4;

      Array.Copy(buffer, index, HashPrevious, 0, 32);
      index += 32;

      Array.Copy(buffer, index, MerkleRoot, 0, 32);
      index += 32;

      Array.Copy(buffer, index, HashDatabase, 0, 32);
      index += 32;

      Nonce = BitConverter.ToUInt32(buffer, index);
      index += 4;
    }

    internal override byte[] Serialize()
    {
      byte[] buffer = new byte[COUNT_HEADER_BYTES];

      BitConverter.GetBytes(Version).CopyTo(buffer, 0);

      HashPrevious.CopyTo(buffer, 4);

      MerkleRoot.CopyTo(buffer, 36);

      HashDatabase.CopyTo(buffer, 68);

      BitConverter.GetBytes(Nonce).CopyTo(buffer, 100);

      return buffer;
    }

    internal override void CopyTo(Header header)
    {
      base.CopyTo(header);
      ((HeaderBToken)header).Version = Version;
      ((HeaderBToken)header).HashDatabase = HashDatabase;
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
}
