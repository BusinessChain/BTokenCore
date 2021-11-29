using System;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBToken : Header
  {
    public HeaderBToken(
      byte[] headerHash,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds) : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        unixTimeSeconds)
    { }

    public override byte[] GetBytes()
    {
      return null;
    }
  }
}
