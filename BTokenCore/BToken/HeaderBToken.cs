using System;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBToken : Header
  {
    public const int COUNT_HEADER_BYTES = 80;

    public byte[] HashAnchorPrevious;

    Header HeaderAnchor;

    public byte[] HashDatabase;


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

    public override void AppendToHeader(Header headerPrevious)
    {
      base.AppendToHeader(headerPrevious);

      HeaderAnchor = ((HeaderBToken)headerPrevious).HeaderAnchor.HeaderNext;

      if (HeaderAnchor == null)
        throw new ProtocolException($"No anchor header found for {this}.");
    }

    public override void Validate()
    {
      // Look into HeaderAnchor and check if the winner Token corresponds to this Header.
    }

    public override byte[] GetBytes()
    {
      return null;
    }
  }
}
