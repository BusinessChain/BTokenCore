using System;
using System.Text;
using System.Collections.Generic;

namespace BTokenLib
{
  public static class VarString
  {
    public static string GetString(byte[] buffer, ref int startIndex)
    {
      int stringLength = VarInt.GetInt(buffer, ref startIndex);
      string text = Encoding.ASCII.GetString(buffer, startIndex, stringLength);

      startIndex += stringLength;
      return text;
    }


    public static List<byte> GetBytes(string text)
    {
      byte[] bytesTextLength = VarInt.GetBytes(text.Length);

      List<byte> serializedValue = new();
      serializedValue.AddRange(bytesTextLength);
      serializedValue.AddRange(Encoding.ASCII.GetBytes(text));

      return serializedValue;
    }
  }
}
