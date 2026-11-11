using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  partial class DatabaseAccounts
  {
    internal class Segment
    {
      static readonly Comparer<byte[]> ComparerID = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));

      ILiteCollection<Account> AccountsStored;
      ILiteCollection<BsonDocument> UndoStored;
      BsonMapper Mapper;
      int Index;


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

      internal Account LoadAccount(byte[] iDAccount, int height)
      {
        Account account = AccountsStored.FindById(iDAccount);

        if (account != null)
          account.Balance -= GetOffsetRent(Index, height);

        return account;
      }

      internal void WriteAccount(Account account, int height)
      {
        AccountsStored.Upsert(new Account
        {
          ID = account.ID,
          HeightAccountCreated = account.HeightAccountCreated,
          Nonce = account.Nonce,
          Balance = account.Balance + GetOffsetRent(Index, height)
        });
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

        foreach (Account account in LoadAccountsUndo(Mapper, documentsUndo))
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
