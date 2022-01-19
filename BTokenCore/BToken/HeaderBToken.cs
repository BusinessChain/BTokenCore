﻿using System;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBToken : Header
  {
    public const int COUNT_HEADER_BYTES = 80;

    public byte[] HashAnchorPrevious;


    public HeaderBToken()
    {
      Buffer = new byte[COUNT_HEADER_BYTES];
    }

    public HeaderBToken(
      byte[] headerHash,
      byte[] hashPrevious,
      byte[] hashAnchorPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds) : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        unixTimeSeconds)
    {
      HashAnchorPrevious = hashAnchorPrevious;
      Difficulty = 1;
    }


    public override byte[] GetBytes()
    {
      return null;
    }
  }
}
