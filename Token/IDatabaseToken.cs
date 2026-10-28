using LiteDB;


namespace BTokenCore;

internal interface IDatabaseToken
{
  internal bool TryLoad(LiteDatabase database);
  internal void Drop();
  internal bool Exists();
  internal byte[] Serialize();
  internal void Install(byte[] buffer, int length);
}
