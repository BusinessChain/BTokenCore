using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  partial class DatabaseAccounts
  {
    class Segment
    {
      const long RENT_PER_BYTE = 1;
      const long RENT_SWEEP = RENT_PER_BYTE * Account.LENGTH_ACCOUNT;

      static readonly Comparer<byte[]> ComparerID = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));

      ILiteCollection<Account> AccountsStored;
      ILiteCollection<BsonDocument> UndoStored;
      BsonMapper Mapper;
      int Index;
      Dictionary<byte[], Account> AccountsUndo = new(new EqualityComparerByteArray());
      List<Account> AccountsSwept = new();


      internal Segment(LiteDatabase liteDatabase, int index)
      {
        AccountsStored = liteDatabase.GetCollection<Account>(NAME_COLLECTION);
        UndoStored = liteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_UNDO);
        Mapper = liteDatabase.Mapper;
        Index = index;
      }

      internal IEnumerable<Account> LoadAccounts()
      {
        return AccountsStored.Query()
          .Where(QueryRange(NAME_FIELD_ID))
          .OrderBy(NAME_FIELD_ID)
          .ToEnumerable();
      }

      long GetOffsetRent(int height)
      {
        return RENT_SWEEP * ((height + COUNT_SEGMENTS - Index) / COUNT_SEGMENTS);
      }

      (List<Account> accountsSwept, long rent) LoadSweep(int height)
      {
        long offsetRent = GetOffsetRent(height);

        List<Account> accountsSwept = new();
        long rent = 0;

        foreach (Account account in LoadAccounts())
          if (account.Balance <= offsetRent)
          {
            accountsSwept.Add(account);
            rent += account.Balance - offsetRent + RENT_SWEEP;
          }
          else
            rent += RENT_SWEEP;

        return (accountsSwept, rent);
      }

      internal (int countAccountsSwept, long rent) ComputeSweep(int height)
      {
        (List<Account> accountsSwept, long rent) = LoadSweep(height);
        return (accountsSwept.Count, rent);
      }

      internal (int countAccountsSwept, long rent) Sweep(int height)
      {
        (AccountsSwept, long rent) = LoadSweep(height);

        foreach (Account account in AccountsSwept)
          AccountsStored.Delete(account.ID);

        return (AccountsSwept.Count, rent);
      }

      internal Account LoadAccount(byte[] iDAccount, int height)
      {
        Account account = AccountsStored.FindById(iDAccount);

        if (account != null)
          account.Balance -= GetOffsetRent(height);

        return account;
      }

      internal void WriteAccount(Account account, int height)
      {
        if (!AccountsUndo.ContainsKey(account.ID))
          AccountsUndo.Add(account.ID, AccountsStored.FindById(account.ID) ?? new Account { ID = account.ID });

        AccountsStored.Upsert(new Account
        {
          ID = account.ID,
          HeightAccountCreated = account.HeightAccountCreated,
          Nonce = account.Nonce,
          Balance = account.Balance + GetOffsetRent(height)
        });
      }

      internal void ClearUndo()
      {
        AccountsUndo.Clear();
        AccountsSwept.Clear();
      }

      internal int FinishBlock(int height)
      {
        int countAccountsDeleted = 0;

        foreach (byte[] iD in AccountsUndo.Keys)
          if (LoadAccount(iD, height).Balance == 0)
          {
            AccountsStored.Delete(iD);
            countAccountsDeleted += 1;
          }

        UndoStored.InsertBulk(
          AccountsUndo.Values.Select(a => CreateDocumentUndo(a, height, isSweep: false))
          .Concat(AccountsSwept.Select(a => CreateDocumentUndo(a, height, isSweep: true))));

        return countAccountsDeleted;
      }

      BsonDocument CreateDocumentUndo(Account account, int height, bool isSweep)
      {
        return new BsonDocument
        {
          [NAME_FIELD_HEIGHT] = height,
          [NAME_FIELD_IS_SWEEP] = isSweep,
          [NAME_FIELD_ACCOUNT] = Mapper.ToDocument(account)
        };
      }

      IEnumerable<Account> LoadAccountsUndo(IEnumerable<BsonDocument> documents)
      {
        return documents.Select(d => Mapper.ToObject<Account>(d[NAME_FIELD_ACCOUNT].AsDocument));
      }

      internal int RollBack(IEnumerable<BsonDocument> documentsUndo)
      {
        int changeCountAccounts = 0;

        foreach (Account account in LoadAccountsUndo(documentsUndo))
        {
          bool isAccountStored = AccountsStored.FindById(account.ID) != null;

          if (account.Balance > 0)
          {
            if (!isAccountStored)
              changeCountAccounts += 1;

            AccountsStored.Upsert(account);
          }
          else if (isAccountStored)
          {
            changeCountAccounts -= 1;
            AccountsStored.Delete(account.ID);
          }
        }

        return changeCountAccounts;
      }

      BsonExpression QueryRange(string pathID)
      {
        byte[] iDFirst = new byte[Crypto.LENGTH_HASH160];
        byte[] iDLast = Enumerable.Repeat((byte)0xFF, Crypto.LENGTH_HASH160).ToArray();

        int prefixFirst = Index << SHIFT_INDEX_SEGMENT;
        int prefixLast = prefixFirst | (1 << SHIFT_INDEX_SEGMENT) - 1;

        iDFirst[0] = (byte)(prefixFirst >> 8);
        iDFirst[1] = (byte)prefixFirst;
        iDLast[0] = (byte)(prefixLast >> 8);
        iDLast[1] = (byte)prefixLast;

        return Query.Between(pathID, iDFirst, iDLast);
      }

      internal byte[] ComputeHash()
      {
        return SHA256.HashData(SerializeAccounts(LoadAccounts()));
      }

      internal byte[] SerializeAtHeight(int height)
      {
        Dictionary<byte[], Account> accountsAtHeight = new(new EqualityComparerByteArray());

        IEnumerable<BsonDocument> documentsUndo = UndoStored.Query()
          .Where(QueryRange(PATH_ID_ACCOUNT_UNDO))
          .Where(Query.GT(NAME_FIELD_HEIGHT, height))
          .ToEnumerable()
          .OrderByDescending(d => d[NAME_FIELD_HEIGHT].AsInt32)
          .ThenBy(d => d[NAME_FIELD_IS_SWEEP].AsBoolean);

        foreach (Account account in LoadAccountsUndo(documentsUndo))
          accountsAtHeight[account.ID] = account;

        IEnumerable<Account> accountsStoredUnchanged = LoadAccounts()
          .Where(a => !accountsAtHeight.ContainsKey(a.ID));

        List<Account> accountsUndoExisting = accountsAtHeight.Values
          .Where(a => a.Balance > 0)
          .OrderBy(a => a.ID, ComparerID)
          .ToList();

        return SerializeAccounts(MergeByID(accountsStoredUnchanged, accountsUndoExisting));
      }

      static IEnumerable<Account> MergeByID(IEnumerable<Account> accountsSorted, List<Account> accountsSortedInsert)
      {
        int i = 0;

        foreach (Account account in accountsSorted)
        {
          for (; i < accountsSortedInsert.Count && ComparerID.Compare(accountsSortedInsert[i].ID, account.ID) < 0; i += 1)
            yield return accountsSortedInsert[i];

          yield return account;
        }

        for (; i < accountsSortedInsert.Count; i += 1)
          yield return accountsSortedInsert[i];
      }

      static byte[] SerializeAccounts(IEnumerable<Account> accounts)
      {
        List<byte> bytes = new();
        int countAccounts = 0;

        foreach (Account account in accounts)
        {
          bytes.AddRange(account.ID);
          bytes.AddRange(BitConverter.GetBytes(account.HeightAccountCreated));
          bytes.AddRange(BitConverter.GetBytes(account.Nonce));
          bytes.AddRange(BitConverter.GetBytes(account.Balance));

          countAccounts += 1;
        }

        return [.. VarInt.GetBytes(countAccounts), .. bytes];
      }

      internal int Install(byte[] segment)
      {
        List<Account> accounts = ParseAccounts(segment);
        AccountsStored.InsertBulk(accounts);
        return accounts.Count;
      }

      internal static bool IsSegmentValid(byte[] segment, int indexSegment)
      {
        List<Account> accounts;

        try
        {
          accounts = ParseAccounts(segment);
        }
        catch (Exception)
        {
          return false;
        }

        for (int i = 0; i < accounts.Count; i += 1)
          if (GetIndexSegment(accounts[i].ID) != indexSegment || i > 0 && ComparerID.Compare(accounts[i - 1].ID, accounts[i].ID) >= 0)
            return false;

        return true;
      }

      static List<Account> ParseAccounts(byte[] segment)
      {
        int index = 0;

        int countAccounts = VarInt.GetInt(segment, ref index);

        if (index + (long)countAccounts * Account.LENGTH_ACCOUNT != segment.Length)
          throw new ProtocolException($"Length {segment.Length} of DB does not match its {countAccounts} accounts.");

        List<Account> accounts = new();

        for (int i = 0; i < countAccounts; i += 1)
          accounts.Add(new()
          {
            ID = segment.ReadBytes(ref index, Crypto.LENGTH_HASH160),
            HeightAccountCreated = segment.Read<int>(ref index),
            Nonce = segment.Read<int>(ref index),
            Balance = segment.Read<long>(ref index)
          });

        return accounts;
      }
    }
  }
}
