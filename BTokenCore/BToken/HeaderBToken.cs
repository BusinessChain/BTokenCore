using System;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBToken : Header
  {
    public const int COUNT_HEADER_BYTES = 80;
    
    public TX TXAnchor;

    public byte[] HashDatabase;
    
    public byte[] HashHeaderAnchor; // not in protocol



    public HeaderBToken()
    {
      Buffer = new byte[COUNT_HEADER_BYTES];
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
      return null;
    }
  }
}
