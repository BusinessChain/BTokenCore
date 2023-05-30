using System;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBToken : Header
  {
    public const int COUNT_HEADER_BYTES = 104;

    // Statt den aktuellen DB Hash, könnte auch der diesem Block vorangehende DB Hash
    // aufgeführt werden. Dies hätte beim Mining der vorteil, dass das Inserten des 
    // gemineden Block nicht durchgespielt werden müsste.

    public byte[] HashDatabase = new byte[32];

    public int IndexTrailAnchor;

    static uint InitializerNonce;



    public HeaderBToken()
    {
      Buffer = new byte[COUNT_HEADER_BYTES];

      Nonce = InitializerNonce++;

      Difficulty = 1;
    }

    public HeaderBToken(
      byte[] headerHash,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds,
      uint nonce) : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        unixTimeSeconds,
        nonce)
    {
      Difficulty = 1;
    }

    public override byte[] GetBytes()
    {
      HashPrevious.CopyTo(Buffer, 0);

      MerkleRoot.CopyTo(Buffer, 32);

      HashDatabase.CopyTo(Buffer, 64);

      BitConverter.GetBytes(UnixTimeSeconds)
        .CopyTo(Buffer, 96);

      BitConverter.GetBytes(Nonce)
        .CopyTo(Buffer, 100);

      return Buffer;
    }
  }
}
