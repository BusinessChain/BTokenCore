using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;


namespace BTokenCore;

public static class ByteArrayExtensionMethods
{
  const string ALPHABET_BASE58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
  const int LENGTH_CHECKSUM_BASE58 = 4;


  public static string ToHexString(this byte[] array)
  {
    return Convert.ToHexString(array.Reverse().ToArray());
  }

  public static string ToHexString(this List<byte> list)
  {
    return ToHexString(list.ToArray());
  }

  public static string BinaryToBase58Check(this byte[] byteArray)
  {
    List<byte> pubKey = byteArray.ToList();
    pubKey.Insert(0, 0x00);

    byte[] checksum = Crypto.ComputeHash256(pubKey.ToArray());

    pubKey.AddRange(checksum.Take(LENGTH_CHECKSUM_BASE58));

    byte[] ba = pubKey.ToArray();

    Org.BouncyCastle.Math.BigInteger addrremain = new(1, ba);
    Org.BouncyCastle.Math.BigInteger big0 = new("0");
    Org.BouncyCastle.Math.BigInteger big58 = new("58");

    string rv = "";

    while (addrremain.CompareTo(big0) > 0)
    {
      int d = Convert.ToInt32(addrremain.Mod(big58).ToString());
      addrremain = addrremain.Divide(big58);
      rv = ALPHABET_BASE58.Substring(d, 1) + rv;
    }

    foreach (byte b in ba)
    {
      if (b != 0) break;
      rv = "1" + rv;
    }

    return rv;
  }

  public static byte[] Base58CheckToBinary(this string address)
  {
    System.Numerics.BigInteger value = 0;

    foreach (char character in address)
    {
      int digit = ALPHABET_BASE58.IndexOf(character);

      if (digit < 0)
        throw new FormatException($"Address {address} contains the character '{character}', which is not Base58.");

      value = value * 58 + digit;
    }

    byte[] bytes = [.. new byte[address.TakeWhile(c => c == ALPHABET_BASE58[0]).Count()], .. value.ToByteArray(isUnsigned: true, isBigEndian: true)];

    int lengthPayload = 1 + Crypto.LENGTH_HASH160;

    if (bytes.Length != lengthPayload + LENGTH_CHECKSUM_BASE58
      || bytes[0] != 0x00
      || !Crypto.ComputeHash256(bytes.AsSpan(0, lengthPayload)).AsSpan(0, LENGTH_CHECKSUM_BASE58).SequenceEqual(bytes.AsSpan(lengthPayload)))
      throw new FormatException($"Address {address} is not a valid Base58Check address.");

    return bytes[1..lengthPayload];
  }

  public static bool IsAllBytesEqual(this byte[] arr1, byte[] arr2, int startIndex2 = 0)
  {
    if (arr2 == null)
      return false;

    for (int i = 0; i < arr1.Length; i++)
      if (arr1[i] != arr2[startIndex2 + i])
        return false;

    return true;
  }

  public static bool IsLessThan(this byte[] arr1, byte[] arr2)
  {
    return arr1.AsSpan().SequenceCompareTo(arr2) < 0;
  }

  public static byte[] ReadBytes(this byte[] buffer, ref int index, int length)
  {
    byte[] bytes = buffer[index..(index + length)];
    index += length;
    return bytes;
  }

  public static T Read<T>(this byte[] buffer, ref int index) where T : struct
  {
    T value = MemoryMarshal.Read<T>(buffer.AsSpan(index));
    index += Unsafe.SizeOf<T>();
    return value;
  }
}
