using LiteDB;


namespace BTokenCore;

internal class TXOutputOwned
{
  [BsonId]
  internal byte[] ID;

  [BsonField]
  internal byte[] TXID;

  [BsonField]
  internal int Index;

  [BsonField]
  internal byte[] IDAccount;

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
