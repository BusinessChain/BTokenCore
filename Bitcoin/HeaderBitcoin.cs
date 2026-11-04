namespace BTokenCore;

internal class HeaderBitcoin : Header
{
  internal const int COUNT_HEADER_BYTES = 80;

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

  internal HeaderBitcoin(byte[] buffer, ref int index)
    : base(buffer, ref index, COUNT_HEADER_BYTES)
  {
    UnixTimeSeconds = buffer.Read<uint>(ref index);

    bool isBlockTimePremature = UnixTimeSeconds >
      (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 * 60 * 60);

    if (isBlockTimePremature)
      throw new ProtocolException($"Timestamp premature {new DateTime(UnixTimeSeconds).Date}.");

    NBits = buffer.Read<uint>(ref index);

    if (Hash.IsGreaterThan(NBits))
      throw new ProtocolException($"Header hash {Hash.ToHexString()} greater than NBits {NBits}.");

    Nonce = buffer.Read<uint>(ref index);

    Difficulty = ComputeDifficultyFromNBits(NBits);
  }

  internal static double ComputeDifficultyFromNBits(uint nBits)
  {
    return MAX_TARGET / (double)UInt256.ParseFromCompact(nBits);
  }

  internal override void AppendToHeader(Header headerPrevious)
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

    base.AppendToHeader(headerPrevious);
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

    ulong actualTimespan = Math.Clamp(
      header.UnixTimeSeconds - headerIntervalStart.UnixTimeSeconds,
      RETARGETING_TIMESPAN_INTERVAL_SECONDS / 4,
      RETARGETING_TIMESPAN_INTERVAL_SECONDS * 4);

    UInt256 targetOld = UInt256.ParseFromCompact(header.NBits);

    UInt256 targetNew = targetOld
      .MultiplyBy(actualTimespan)
      .DivideBy(RETARGETING_TIMESPAN_INTERVAL_SECONDS);

    return UInt256.Min(DIFFICULTY_1_TARGET, targetNew).GetCompact();
  }

  internal override byte[] Serialize()
  {
    byte[] buffer = SerializePrefix(COUNT_HEADER_BYTES);

    BitConverter.GetBytes(UnixTimeSeconds).CopyTo(buffer, 68);

    BitConverter.GetBytes(NBits).CopyTo(buffer, 72);

    BitConverter.GetBytes(Nonce).CopyTo(buffer, 76);

    return buffer;
  }
}