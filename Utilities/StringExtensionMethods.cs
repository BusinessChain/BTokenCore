using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;


namespace BTokenCore;

public static class StringExtensionMethods
{
  public static bool TryMoveDirectoryTo(this string pathSource, string pathDest)
  {
    if (!Directory.Exists(pathSource))
      return false;

    while (true)
      try
      {
        if (Directory.Exists(pathDest))
          Directory.Delete(pathDest, true);

        Directory.Move(pathSource, pathDest);

        return true;
      }
      catch (Exception ex)
      {
        Console.WriteLine(
          $"{ex.GetType().Name} when attempting " +
          $"to move directory:\n{ex.Message}");

        Thread.Sleep(10000);
      }
  }

  public static byte[] Base58ToByteArray(this string base58)
  {
    Org.BouncyCastle.Math.BigInteger bi2 = new Org.BouncyCastle.Math.BigInteger("0");
    string b58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    foreach (char c in base58)
    {
      if (b58.IndexOf(c) != -1)
      {
        bi2 = bi2.Multiply(new Org.BouncyCastle.Math.BigInteger("58"));
        bi2 = bi2.Add(new Org.BouncyCastle.Math.BigInteger(b58.IndexOf(c).ToString()));
      }
      else
      {
        return null;
      }
    }

    byte[] bb = bi2.ToByteArrayUnsigned();

    // interpret leading '1's as leading zero bytes
    foreach (char c in base58)
    {
      if (c != '1') break;
      byte[] bbb = new byte[bb.Length + 1];
      Array.Copy(bb, 0, bbb, 1, bb.Length);
      bb = bbb;
    }

    return bb;
  }

  public static byte[] ToBinary(this string hexString)
  {
    byte[] bytes = Convert.FromHexString(hexString);
    Array.Reverse(bytes);
    return bytes;
  }

  public static byte[] Base58CheckToPubKeyHash(this string base58)
  {
    byte[] bb = base58.Base58ToByteArray();

    byte[] checksum = Crypto.ComputeHash256(bb.AsSpan(0, bb.Length - 4));

    for (int i = 0; i < 4; i++)
      if (checksum[i] != bb[bb.Length - 4 + i])
        throw new Exception($"Invalid checksum in address {base58}.");

    byte[] rv = new byte[bb.Length - 5];
    Array.Copy(bb, 1, rv, 0, bb.Length - 5);
    return rv;
  }

  public static string GetIPFromFileName(this string fileName)
  {
    return new string(fileName.TakeWhile(c => c != '-').ToArray());
  }
}