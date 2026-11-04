namespace BTokenCore;


internal partial class Network
{
  internal class Inventory
  {
    internal enum InventoryType
    {
      UNDEFINED = 0,
      MSG_TX = 1,
      MSG_BLOCK = 2,
      MSG_FILTERED_BLOCK = 3,
      MSG_CMPCT_BLOCK = 4,
      MSG_ACCOUNTS = 5
    }

    internal const int LENGTH = 36;

    internal InventoryType Type;
    internal byte[] Hash;

    internal Inventory(InventoryType type, byte[] hash)
    {
      Type = type;
      Hash = hash;
    }

    internal byte[] GetBytes()
    {
      return [.. BitConverter.GetBytes((uint)Type), .. Hash];
    }

    internal static Inventory Parse(byte[] buffer, ref int startIndex)
    {
      uint type = buffer.Read<uint>(ref startIndex);

      byte[] hash = buffer.ReadBytes(ref startIndex, Crypto.LENGTH_HASH256);

      return new Inventory((InventoryType)type, hash);
    }
  }
}
