using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;


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

  public static T Read<T>(this byte[] buffer, ref int index) where T : struct
  {
    T value = MemoryMarshal.Read<T>(buffer.AsSpan(index));
    index += Unsafe.SizeOf<T>();
    return value;
  }
}
