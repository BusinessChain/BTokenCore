using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  public class HeaderBToken : Header
  {
    internal const int COUNT_HEADER_BYTES = 100;

    internal byte[] HashDatabase = new byte[32];


    internal HeaderBToken()
    {
      Difficulty = 1;

      BlockRewardInitial = 200000000000000; // 200 BTK;
      PeriodHalveningBlockReward = 105000;
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

      uint version = BitConverter.ToUInt32(buffer, index);
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

      HashPrevious.CopyTo(buffer, 0);

      MerkleRoot.CopyTo(buffer, 32);

      HashDatabase.CopyTo(buffer, 64);

      BitConverter.GetBytes(Nonce).CopyTo(buffer, 96);

      return buffer;
    }

    internal override void CopyTo(Header header)
    {
      base.CopyTo(header);
      ((HeaderBToken)header).HashDatabase = HashDatabase;
    }

    internal override void VerifyCoinbase(long valueOutputsTXCoinbase)
    {
      long blockReward = BlockRewardInitial >> Height / PeriodHalveningBlockReward;

      if (blockReward + Fee != valueOutputsTXCoinbase)
        throw new ProtocolException($"Output values of coinbase not equal to blockReward plus tx fees.");
    }
  }
}
