using LiteDB;


namespace BTokenCore;

internal class TXOutputOwned
{
  [BsonId]
  internal byte[] ID;

  [BsonField]
  internal byte[] TXID = new byte[32];

  [BsonField]
  internal int Index;

  [BsonField]
  internal long Value;

  [BsonField]
  internal int HeightCreated;

  [BsonField]
  internal int HeightSpent;


  internal static byte[] CreateID(byte[] tXID, int index)
  {
    return [.. tXID, .. BitConverter.GetBytes(index)];
  }
}
