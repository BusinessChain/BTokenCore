using System;
using System.Linq;
using System.Collections.Generic;


namespace BTokenCore;

public static class ByteArrayExtensionMethods
{
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

    pubKey.AddRange(checksum.Take(4));

    byte[] ba = pubKey.ToArray();

    Org.BouncyCastle.Math.BigInteger addrremain = new(1, ba);
    Org.BouncyCastle.Math.BigInteger big0 = new("0");
    Org.BouncyCastle.Math.BigInteger big58 = new("58");

    string b58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    string rv = "";

    while (addrremain.CompareTo(big0) > 0)
    {
      int d = Convert.ToInt32(addrremain.Mod(big58).ToString());
      addrremain = addrremain.Divide(big58);
      rv = b58.Substring(d, 1) + rv;
    }

    foreach (byte b in ba)
    {
      if (b != 0) break;
      rv = "1" + rv;
    }

    return rv;
  }

  public static bool IsAllBytesEqual(this byte[] arr1, byte[] arr2, int startIndex2 = 0)
  {
    for (int i = 0; i < arr1.Length; i++)
      if (arr1[i] != arr2[startIndex2 + i])
        return false;

    return true;
  }

  public static byte[] ReadBytes(this byte[] buffer, ref int index, int length)
  {
    byte[] bytes = buffer[index..(index + length)];
    index += length;
    return bytes;
  }

  public static bool IsGreaterThan(this byte[] array, uint nBits)
  {
    int expBits = ((int)nBits & 0x7F000000) >> 24;
    UInt32 factorBits = nBits & 0x00FFFFFF;

    if (expBits < 3)
      factorBits >>= (3 - expBits) * 8;

    var bytes = new List<byte>();

    for (int i = expBits - 3; i > 0; i--)
      bytes.Add(0x00);

    bytes.Add((byte)(factorBits & 0xFF));
    bytes.Add((byte)((factorBits & 0xFF00) >> 8));
    bytes.Add((byte)((factorBits & 0xFF0000) >> 16));

    return array.IsGreaterThan(bytes.ToArray());
  }

  public static bool IsGreaterThan(this byte[] a1, byte[] a2)
  {
    int i = a1.Length;

    while (i-- > a2.Length)
      if (a1[i] > 0)
        return true;

    while (a1[i] == a2[i])
    {
      i -= 1;

      if (i < 0)
        return false;
    }

    return a1[i] > a2[i];
  }

  public static void Increment(this byte[] array, int startIndex, int length)
  {
    int offset = 0;
    int index;

    while (offset < length)
    {
      index = startIndex + offset;

      array[index] += 1;

      if (array[index] != 0)
        return;

      offset += 1;
    }
  }

  public static byte[] SubtractByteWise(this byte[] array1, byte[] array2)
  {
    byte[] difference = new byte[array1.Length];

    for (int i = 0; i < array1.Length; i++)
      difference[i] = (byte)(array1[i] - array2[i]);

    return difference;
  }
}
