using System;
using System.Linq;


namespace BTokenCore;

public partial class TokenBToken : Token
{
  public class HeaderBToken : Header
  {
    internal const int COUNT_HEADER_BYTES = 100;

    // Statt den aktuellen DB Hash, könnte auch der diesem Block vorangehende DB Hash
    // aufgeführt werden. Dies hätte beim Mining der vorteil, dass das Inserten des 
    // gemineden Block nicht durchgespielt werden müsste.

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
      uint nonce) : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        nonce)
    {
      Difficulty = 1;
      HashDatabase = hashDatabase;

      BlockRewardInitial = 200000000000000; // 200 BTK;
      PeriodHalveningBlockReward = 105000;
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

    internal override bool TryAppendToHeader(Header headerPrevious)
    {
      Header headerParent = headerPrevious.HeaderParent.HeaderNext;

      while (true)
      {
        if (headerParent == null)
          return false;

        if (headerParent.AnchorsWinner.Any(a => a.Value.HashBlockReferenced.IsAllBytesEqual(Hash)))
        {
          HeaderParent = headerParent;
          break;
        }

        headerParent = headerParent.HeaderNext;
      }

      return base.TryAppendToHeader(headerPrevious);
    }

    internal override void VerifyCoinbase(long valueOutputsTXCoinbase)
    {
      long blockReward = BlockRewardInitial >> Height / PeriodHalveningBlockReward;

      if (blockReward + Fee != valueOutputsTXCoinbase)
        throw new ProtocolException($"Output values of coinbase not equal to blockReward plus tx fees.");
    }
  }
}
