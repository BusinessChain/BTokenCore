using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  internal class DatabaseAccounts
  {
    const string NAME_COLLECTION = "accounts";

    LiteDatabase Database;
    ILiteCollection<Account> Collection;


    internal bool TryLoad(LiteDatabase database)
    {
      Database = database;

      bool isStored = Database.CollectionExists(NAME_COLLECTION);
      Collection = Database.GetCollection<Account>(NAME_COLLECTION);

      return isStored;
    }

    internal void Drop()
    {
      Database.DropCollection(NAME_COLLECTION);
      Collection = null;
    }

    internal bool Exists()
    {
      return Collection != null;
    }

    internal byte[] Serialize()
    {
      List<Account> accounts = Collection.Find(Query.All(Query.Ascending)).ToList();

      List<byte> bytes = new();

      bytes.AddRange(VarInt.GetBytes(accounts.Count));

      foreach (Account account in accounts)
      {
        bytes.AddRange(account.ID);
        bytes.AddRange(BitConverter.GetBytes(account.BlockHeightAccountCreated));
        bytes.AddRange(BitConverter.GetBytes(account.Nonce));
        bytes.AddRange(BitConverter.GetBytes(account.Balance));
      }

      return bytes.ToArray();
    }

    internal void Install(byte[] buffer, int length)
    {
      int index = 0;

      int countAccounts = VarInt.GetInt(buffer, ref index);

      if (index + (long)countAccounts * Account.LENGTH_ACCOUNT != length)
        throw new ProtocolException($"Length {length} of DB does not match its {countAccounts} accounts.");

      List<Account> accounts = new();

      for (int i = 0; i < countAccounts; i += 1)
      {
        Account account = new();

        Array.Copy(buffer, index, account.ID, 0, Account.LENGTH_ID);
        index += Account.LENGTH_ID;

        account.BlockHeightAccountCreated = BitConverter.ToInt32(buffer, index);
        index += 4;

        account.Nonce = BitConverter.ToInt32(buffer, index);
        index += 4;

        account.Balance = BitConverter.ToInt64(buffer, index);
        index += 8;

        accounts.Add(account);
      }

      Collection = Database.GetCollection<Account>(NAME_COLLECTION);
      Collection.InsertBulk(accounts);
    }

    internal Account LoadAccount(byte[] accountID, int blockHeightAccountCreated)
    {
      if (Collection.FindById(accountID) is Account accountStored)
        return accountStored;

      return new()
      {
        ID = accountID,
        BlockHeightAccountCreated = blockHeightAccountCreated
      };
    }

    internal Account GetCopyOfAccount(byte[] accountID)
    {
      if (Collection.FindById(accountID) is Account accountStored)
        return new(accountStored);
      else
        throw new ProtocolException($"Account {accountID.ToHexString()} not found in database.");
    }

    internal void WriteAccount(Account account)
    {
      if (account.Balance > 0)
        Collection.Upsert(account);
      else
        Collection.Delete(account.ID);
    }
  }
}
