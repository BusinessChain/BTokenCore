using System;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBToken : Header
  {
    public const int COUNT_HEADER_BYTES = 80;
    
    public TX TXAnchor;
    public Header HeaderAnchor;

    public byte[] HashDatabase;



    public HeaderBToken()
    {
      Buffer = new byte[COUNT_HEADER_BYTES];
    }

    public HeaderBToken(
      byte[] headerHash,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds) : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        unixTimeSeconds)
    {
      Difficulty = 1;
    }

    public override byte[] GetBytes()
    {
      return null;
    }
  }
}
