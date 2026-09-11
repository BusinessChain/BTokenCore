using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;


namespace BTokenCore;

internal class Inventory
{
  internal enum InventoryType
  {
    UNDEFINED = 0,
    MSG_TX = 1,
    MSG_BLOCK = 2,
    MSG_FILTERED_BLOCK = 3,
    MSG_CMPCT_BLOCK = 4,
    MSG_DB = 5
  }

  internal InventoryType Type;
  internal byte[] Hash;

  internal Inventory(InventoryType type, byte[] hash)
  {
    Type = type;
    Hash = hash;
  }

  internal List<byte> GetBytes()
  {
    List<byte> bytes = new List<byte>();

    bytes.AddRange(BitConverter.GetBytes((uint)Type));
    bytes.AddRange(Hash);

    return bytes;
  }

  internal static Inventory Parse(
    byte[] buffer,
    ref int startIndex)
  {
    uint type = BitConverter.ToUInt32(buffer, startIndex);
    startIndex += 4;

    byte[] hash = new byte[32];
    Array.Copy(buffer, startIndex, hash, 0, 32);
    startIndex += 32;

    return new Inventory(
      (InventoryType)type,
      hash);
  }

  internal bool IsTX()
  {
    return Type == InventoryType.MSG_TX;
  }
}
