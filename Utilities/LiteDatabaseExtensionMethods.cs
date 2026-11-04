using LiteDB;


namespace BTokenCore;

public static class LiteDatabaseExtensionMethods
{
  public static void RunTransaction(this LiteDatabase liteDatabase, Action action)
  {
    liteDatabase.BeginTrans();

    try
    {
      action();
    }
    catch
    {
      liteDatabase.Rollback();
      throw;
    }

    liteDatabase.Commit();
  }
}
