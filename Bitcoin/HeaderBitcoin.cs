namespace BTokenCore;

internal class HeaderBitcoin : Header
{
  internal const int COUNT_HEADER_BYTES = 80;

  internal uint NBits;

  internal uint UnixTimeSeconds;

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
  }

  internal HeaderBitcoin(byte[] buffer, ref int index)
    : base(buffer, ref index, COUNT_HEADER_BYTES)
  {
    UnixTimeSeconds = buffer.Read<uint>(ref index);

    bool isBlockTimePremature = UnixTimeSeconds >
      (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 * 60 * 60);

    if (isBlockTimePremature)
      throw new ProtocolException($"Timestamp premature {DateTimeOffset.FromUnixTimeSeconds(UnixTimeSeconds)}.");

    NBits = buffer.Read<uint>(ref index);

    if (new UInt256(Hash).IsGreaterThan(UInt256.ParseFromCompact(NBits)))
      throw new ProtocolException($"Header hash {Hash.ToHexString()} greater than NBits {NBits}.");

    Nonce = buffer.Read<uint>(ref index);
  }

  internal override void AppendToHeader(Header headerPrevious)
  {
    uint medianTimePastSeconds = GetMedianTimePastSeconds(headerPrevious as HeaderBitcoin);

    if (UnixTimeSeconds < medianTimePastSeconds)
      throw new ProtocolException(
        $"Header {this} with unix time {DateTimeOffset.FromUnixTimeSeconds(UnixTimeSeconds)} " +
        $"is older than median time past {DateTimeOffset.FromUnixTimeSeconds(medianTimePastSeconds)}.");

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

    ulong actualTimespan = (ulong)Math.Clamp(
      (long)header.UnixTimeSeconds - headerIntervalStart.UnixTimeSeconds,
      (long)RETARGETING_TIMESPAN_INTERVAL_SECONDS / 4,
      (long)RETARGETING_TIMESPAN_INTERVAL_SECONDS * 4);

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