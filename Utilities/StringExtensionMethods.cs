namespace BTokenCore;

public static class StringExtensionMethods
{
  public static byte[] ToBinary(this string hexString)
  {
    byte[] bytes = Convert.FromHexString(hexString);
    Array.Reverse(bytes);
    return bytes;
  }
}