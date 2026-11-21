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
