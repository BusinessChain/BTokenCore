using System;
using System.Linq;


namespace BTokenLib
{
  public partial class TokenBToken : Token
  {
    public class HeaderBToken : Header
    {
      public const int COUNT_HEADER_BYTES = 100;

      // Statt den aktuellen DB Hash, könnte auch der diesem Block vorangehende DB Hash
      // aufgeführt werden. Dies hätte beim Mining der vorteil, dass das Inserten des 
      // gemineden Block nicht durchgespielt werden müsste.

      public byte[] HashDatabase = new byte[32];


      public HeaderBToken()
      {
        Difficulty = 1;
      }

      public HeaderBToken(
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

      public override byte[] Serialize()
      {
        byte[] buffer = new byte[COUNT_HEADER_BYTES];

        HashPrevious.CopyTo(buffer, 0);

        MerkleRoot.CopyTo(buffer, 32);

        HashDatabase.CopyTo(buffer, 64);

        BitConverter.GetBytes(Nonce).CopyTo(buffer, 96);

        return buffer;
      }

      public override Header AppendToHeader(Header headerPrevious)
      {
        if (headerPrevious.HeaderParent != null)
        {
          Header headerParent = headerPrevious.HeaderParent.HeaderNext;

          while (true)
          {
            if (headerParent == null)
              throw new ProtocolException($"Cannot append header {this} to header {headerPrevious} because it is not anchored in parent chain.");

            if (headerParent.HashesChild.Any(h => h.Value.IsAllBytesEqual(Hash)))
            {
              HeaderParent = headerParent;
              break;
            }

            headerParent = headerParent.HeaderNext;
          }
        }

        return base.AppendToHeader(headerPrevious);
      }

      public override void VerifyCoinbase(long valueOutputsTXCoinbase)
      {
        long blockReward = BlockRewardInitial >> Height / PeriodHalveningBlockReward;

        if (blockReward + Fee != valueOutputsTXCoinbase)
          throw new ProtocolException($"Output values of coinbase not equal to blockReward plus tx fees.");
      }
    }
  }
}
