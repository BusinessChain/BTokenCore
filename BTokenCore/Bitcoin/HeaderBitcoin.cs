using System;
using System.Collections.Generic;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  class HeaderBitcoin : Header
  {
    public const int COUNT_HEADER_BYTES = 80;

    public uint Version;
    public uint NBits;

    const double MAX_TARGET = 2.695994666715064E67;
    const int RETARGETING_BLOCK_INTERVAL = 2016;
    const ulong RETARGETING_TIMESPAN_INTERVAL_SECONDS = 14 * 24 * 60 * 60;

    static readonly UInt256 DIFFICULTY_1_TARGET = new UInt256(
      "00000000FFFF0000000000000000000000000000000000000000000000000000".ToBinary());



    public HeaderBitcoin()
    {
      Buffer = new byte[COUNT_HEADER_BYTES];
    }

    public HeaderBitcoin(
      byte[] headerHash,
      uint version,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds,
      uint nBits,
      int nonce ) 
      : base(
        headerHash,
        hashPrevious,
        merkleRootHash,
        unixTimeSeconds,
        nonce)
    {
      Buffer = new byte[COUNT_HEADER_BYTES];

      Version = version;
      NBits = nBits;

      ComputeDifficultyFromNBits();
    }

    void ComputeDifficultyFromNBits()
    {
      Difficulty = MAX_TARGET /
        (double)UInt256.ParseFromCompact(NBits);
    }

    public override void AppendToHeader(
      Header headerTip,
      SHA256 sHA256)
    {
      var headerBitcoinTip = (HeaderBitcoin)headerTip;

      Version = headerBitcoinTip.Version;

      NBits = GetNextTarget(headerBitcoinTip);
      ComputeDifficultyFromNBits();

      base.AppendToHeader(
        headerTip,
        sHA256);
    }

    public override void AppendToHeader(Header headerPrevious)
    {
      base.AppendToHeader(headerPrevious);

      uint medianTimePastSeconds = GetMedianTimePastSeconds(HeaderPrevious);

      if (UnixTimeSeconds < medianTimePastSeconds)
        throw new ProtocolException(string.Format(
          $"Header {this} with unix time {1} " +
          "is older than median time past {2}.",
          DateTimeOffset.FromUnixTimeSeconds(UnixTimeSeconds),
          DateTimeOffset.FromUnixTimeSeconds(medianTimePastSeconds)));

      uint targetBitsNew = GetNextTarget((HeaderBitcoin)HeaderPrevious);

      if (NBits != targetBitsNew)
        throw new ProtocolException(
          $"nBits {NBits} not equal to target nBits {targetBitsNew}\n" +
          $"in header {this}.");
    }

    static uint GetMedianTimePastSeconds(Header header)
    {
      const int MEDIAN_TIME_PAST = 11;

      List<uint> timestampsPast = new();

      int depth = 0;
      while (depth < MEDIAN_TIME_PAST)
      {
        timestampsPast.Add(header.UnixTimeSeconds);

        if (header.HeaderPrevious == null)
          break;

        header = header.HeaderPrevious;
        depth++;
      }

      timestampsPast.Sort();

      return timestampsPast[timestampsPast.Count / 2];
    }

    static uint GetNextTarget(HeaderBitcoin header)
    {
      if (((header.Height + 1) % RETARGETING_BLOCK_INTERVAL) != 0)
        return header.NBits;

      Header headerIntervalStart = header;
      int depth = RETARGETING_BLOCK_INTERVAL;

      while (
        --depth > 0 &&
        headerIntervalStart.HeaderPrevious != null)
      {
        headerIntervalStart = headerIntervalStart.HeaderPrevious;
      }

      ulong actualTimespan = Limit(
        header.UnixTimeSeconds -
        headerIntervalStart.UnixTimeSeconds);

      UInt256 targetOld = UInt256.ParseFromCompact(header.NBits);

      UInt256 targetNew = targetOld
        .MultiplyBy(actualTimespan)
        .DivideBy(RETARGETING_TIMESPAN_INTERVAL_SECONDS);

      return UInt256.Min(DIFFICULTY_1_TARGET, targetNew)
        .GetCompact();
    }

    static ulong Limit(ulong actualTimespan)
    {
      if (actualTimespan < RETARGETING_TIMESPAN_INTERVAL_SECONDS / 4)
      {
        return RETARGETING_TIMESPAN_INTERVAL_SECONDS / 4;
      }

      if (actualTimespan > RETARGETING_TIMESPAN_INTERVAL_SECONDS * 4)
      {
        return RETARGETING_TIMESPAN_INTERVAL_SECONDS * 4;
      }

      return actualTimespan;
    }

    public override byte[] GetBytes()
    {
      BitConverter.GetBytes(Version)
        .CopyTo(Buffer, 0);

      HashPrevious.CopyTo(Buffer, 4);

      MerkleRoot.CopyTo(Buffer, 36);

      BitConverter.GetBytes(UnixTimeSeconds)
        .CopyTo(Buffer, 68);

      BitConverter.GetBytes(NBits)
        .CopyTo(Buffer, 72);

      BitConverter.GetBytes(Nonce)
        .CopyTo(Buffer, 76);

      return Buffer;
    }
  }
}
