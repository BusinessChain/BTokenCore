namespace BTokenCore;


public static class VarInt
{
  public const byte PREFIX_UINT16 = 0XFD;
  public const byte PREFIX_UINT32 = 0XFE;
  public const byte PREFIX_UINT64 = 0XFF;


  public static byte[] GetBytes(int value)
  {
    return GetBytes((ulong)value);
  }

  public static byte[] GetBytes(ulong value)
  {
    List<byte> serializedValue = new();

    byte prefix;
    int length;
    AssignPrefixAndLength(value, out prefix, out length);

    byte[] valueBytes = new byte[length];
    valueBytes[0] = prefix;

    for (int i = 1; i < length; i++)
    {
      byte nextByte = (byte)(value >> 8 * (i - 1));
      valueBytes[i] = nextByte;
    }

    return valueBytes;
  }

  static void AssignPrefixAndLength(ulong value, out byte prefix, out int length)
  {
    if (value < PREFIX_UINT16)
    {
      prefix = (byte)value;
      length = 1;
    }
    else if (value <= 0xFFFF)
    {
      prefix = PREFIX_UINT16;
      length = 3;
    }
    else if (value <= 0xFFFFFFFF)
    {
      prefix = PREFIX_UINT32;
      length = 5;
    }
    else
    {
      prefix = PREFIX_UINT64;
      length = 9;
    }
  }

  public static int GetInt(byte[] buffer, ref int startIndex)
  {
    byte prefix = buffer[startIndex];
    startIndex++;

    ulong value;

    if (prefix < 0xfd)
    {
      value = prefix;
    }
    else if (prefix == PREFIX_UINT16)
    {
      value = buffer.Read<ushort>(ref startIndex);
    }
    else if (prefix == PREFIX_UINT32)
    {
      value = buffer.Read<uint>(ref startIndex);
    }
    else // PREFIX_UINT64 (0xff)
    {
      value = buffer.Read<ulong>(ref startIndex);
    }

    if (value > int.MaxValue)
      throw new ProtocolException($"VarInt too large: {value}");

    return (int)value;
  }
}
