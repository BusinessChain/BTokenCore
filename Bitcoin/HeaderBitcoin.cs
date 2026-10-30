using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  internal class HeaderBitcoin : Header
  {
    internal const int COUNT_HEADER_BYTES = 80;

    internal uint Version;
    internal uint NBits;

    internal uint UnixTimeSeconds;

    const double MAX_TARGET = 2.695994666715064E67;
    const int RETARGETING_BLOCK_INTERVAL = 2016;
    const ulong RETARGETING_TIMESPAN_INTERVAL_SECONDS = 14 * 24 * 60 * 60;

    static readonly UInt256 DIFFICULTY_1_TARGET = new UInt256(
      "00000000FFFF0000000000000000000000000000000000000000000000000000".ToBinary());


    internal HeaderBitcoin(
      byte[] headerHash,
      uint version,
      byte[] hashPrevious,
      byte[] merkleRootHash,
      uint unixTimeSeconds,
      uint nBits,
      uint nonce)
    {
      Hash = headerHash;
      Version = version;
      HashPrevious = hashPrevious;
      MerkleRoot = merkleRootHash;
      UnixTimeSeconds = unixTimeSeconds;
      NBits = nBits;
      Nonce = nonce;

      Difficulty = ComputeDifficultyFromNBits(nBits);
    }

    internal HeaderBitcoin(byte[] buffer, ref int index, SHA256 sHA256)
    {
      Hash = sHA256.ComputeHash(
        sHA256.ComputeHash(
          buffer,
          index,
          COUNT_HEADER_BYTES));

      Version = BitConverter.ToUInt32(buffer, index);
      index += 4;

      Array.Copy(buffer, index, HashPrevious, 0, 32);
      index += 32;

      Array.Copy(buffer, index, MerkleRoot, 0, 32);
      index += 32;

      UnixTimeSeconds = BitConverter.ToUInt32(buffer, index);
      index += 4;

      bool isBlockTimePremature = UnixTimeSeconds >
        (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 * 60 * 60);

      if (isBlockTimePremature)
        throw new ProtocolException($"Timestamp premature {new DateTime(UnixTimeSeconds).Date}.");

      NBits = BitConverter.ToUInt32(buffer, index);
      index += 4;

      if (Hash.IsGreaterThan(NBits))
        throw new ProtocolException($"Header hash {Hash.ToHexString()} greater than NBits {NBits}.");

      Nonce = BitConverter.ToUInt32(buffer, index);
      index += 4;

      Difficulty = ComputeDifficultyFromNBits(NBits);
    }

    internal static double ComputeDifficultyFromNBits(uint nBits)
    {
      return MAX_TARGET / (double)UInt256.ParseFromCompact(nBits);
    }

    internal override bool TryAppendToHeader(Header headerPrevious)
    {
      uint medianTimePastSeconds = GetMedianTimePastSeconds(headerPrevious as HeaderBitcoin);

      if (UnixTimeSeconds < medianTimePastSeconds)
        throw new ProtocolException(string.Format(
          $"Header {this} with unix time {1} is older than median time past {2}.",
          DateTimeOffset.FromUnixTimeSeconds(UnixTimeSeconds),
          DateTimeOffset.FromUnixTimeSeconds(medianTimePastSeconds)));

      uint targetBitsNew = GetNextTarget((HeaderBitcoin)headerPrevious);

      if (NBits != targetBitsNew)
        throw new ProtocolException(
          $"nBits {NBits} not equal to target nBits {targetBitsNew} in header {this}.");

      return base.TryAppendToHeader(headerPrevious);
    }

    static uint GetMedianTimePastSeconds(HeaderBitcoin header)
    {
      const int MEDIAN_TIME_PAST = 11;

      List<uint> timestampsPast = new();

      int depth = 0;
      while (depth < MEDIAN_TIME_PAST)
      {
        timestampsPast.Add(header.UnixTimeSeconds);

        if (header.HeaderPrevious == null)
          break;

        header = header.HeaderPrevious as HeaderBitcoin;
        depth++;
      }

      timestampsPast.Sort();

      return timestampsPast[timestampsPast.Count / 2];
    }

    static uint GetNextTarget(HeaderBitcoin header)
    {
      if (((header.Height + 1) % RETARGETING_BLOCK_INTERVAL) != 0)
        return header.NBits;

      HeaderBitcoin headerIntervalStart = header;
      int depth = RETARGETING_BLOCK_INTERVAL;

      while (--depth > 0 && headerIntervalStart.HeaderPrevious != null)
        headerIntervalStart = headerIntervalStart.HeaderPrevious as HeaderBitcoin;

      ulong actualTimespan = Limit(
        header.UnixTimeSeconds -
        headerIntervalStart.UnixTimeSeconds);

      UInt256 targetOld = UInt256.ParseFromCompact(header.NBits);

      UInt256 targetNew = targetOld
        .MultiplyBy(actualTimespan)
        .DivideBy(RETARGETING_TIMESPAN_INTERVAL_SECONDS);

      return UInt256.Min(DIFFICULTY_1_TARGET, targetNew).GetCompact();
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

    internal override byte[] Serialize()
    {
      byte[] buffer = new byte[COUNT_HEADER_BYTES];

      BitConverter.GetBytes(Version).CopyTo(buffer, 0);

      HashPrevious.CopyTo(buffer, 4);

      MerkleRoot.CopyTo(buffer, 36);

      BitConverter.GetBytes(UnixTimeSeconds).CopyTo(buffer, 68);

      BitConverter.GetBytes(NBits).CopyTo(buffer, 72);

      BitConverter.GetBytes(Nonce).CopyTo(buffer, 76);

      return buffer;
    }

    internal void IncrementNonce(uint nonceSeed)
    {
      Nonce += 1;

      if (Nonce == 0)
        Nonce = nonceSeed;
    }
  }
}
