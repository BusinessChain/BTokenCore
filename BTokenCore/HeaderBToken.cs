using System;
using System.Security.Cryptography;



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
  }
}
