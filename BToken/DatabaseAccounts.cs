using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class DatabaseAccounts
  {
    const string NAME_COLLECTION = "accounts";
    const string NAME_COLLECTION_UNDO = "accountsUndo";
    const string NAME_COLLECTION_COMPLETE = "accountsComplete";
    const string NAME_FIELD_ACCOUNT = "account";
    const string PATH_ID_ACCOUNT_UNDO = "$." + NAME_FIELD_ACCOUNT + "." + NAME_FIELD_ID;

    LiteDatabase LiteDatabase;
    ILiteCollection<Account> AccountsStored;
    ILiteCollection<BsonDocument> UndoStored;
    Dictionary<byte[], Account> AccountsUndo = new(new EqualityComparerByteArray());

    static readonly Comparer<byte[]> ComparerID = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));


    internal bool TryLoad(LiteDatabase storage)
    {
      LiteDatabase = storage;
      UndoStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_UNDO);

      bool isComplete = LiteDatabase.CollectionExists(NAME_COLLECTION_COMPLETE);

      if (!isComplete)
      {
        Drop();
        MarkComplete();
      }

      OpenAccounts();

      return isComplete;
    }

    void OpenAccounts()
    {
      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);
      UndoStored.EnsureIndex(NAME_FIELD_HEIGHT);
      UndoStored.EnsureIndex(PATH_ID_ACCOUNT_UNDO);
    }

    internal void MarkComplete()
    {
      LiteDatabase.GetCollection(NAME_COLLECTION_COMPLETE).Upsert(new BsonDocument { [NAME_FIELD_ID] = 0 });
    }

    internal void Drop()
    {
      LiteDatabase.DropCollection(NAME_COLLECTION);
      LiteDatabase.DropCollection(NAME_COLLECTION_UNDO);
      LiteDatabase.DropCollection(NAME_COLLECTION_COMPLETE);
      AccountsStored = null;
    }

    internal bool Exists()
    {
      return AccountsStored != null;
    }

    internal List<byte[]> SerializeSegmentsBelowWindow()
    {
      Dictionary<byte[], Account> accountsBelowWindow = new(new EqualityComparerByteArray());

      foreach (Account account in LoadAccountsUndo(UndoStored.Query().OrderByDescending(NAME_FIELD_HEIGHT)))
        accountsBelowWindow[account.ID] = account;

      IEnumerable<Account> accountsStoredUnchanged = AccountsStored.Find(Query.All(Query.Ascending))
        .Where(a => !accountsBelowWindow.ContainsKey(a.ID));

      List<Account> accountsUndoExisting = accountsBelowWindow.Values
        .Where(a => a.Balance > 0)
        .OrderBy(a => a.ID, ComparerID)
        .ToList();

      using IEnumerator<Account> accounts = MergeByID(accountsStoredUnchanged, accountsUndoExisting).GetEnumerator();

      bool hasAccount = accounts.MoveNext();
      List<byte[]> segments = new();

      for (int segment = 0; segment < COUNT_SEGMENTS; segment += 1)
      {
        List<Account> accountsSegment = new();

        for (; hasAccount && accounts.Current.ID[0] == segment; hasAccount = accounts.MoveNext())
          accountsSegment.Add(accounts.Current);

        segments.Add(SerializeAccounts(accountsSegment));
      }

      return segments;
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

    internal void Install(IEnumerable<byte[]> segments)
    {
      OpenAccounts();

      foreach (byte[] segment in segments.Where(s => s != null))
        AccountsStored.InsertBulk(ParseAccounts(segment, segment.Length));
    }

    internal byte[][] AdvanceSegments(byte[][] segments, List<Block> blocks)
    {
      LiteDatabase.BeginTrans();

      try
      {
        Install(segments);

        foreach (Block block in blocks)
          InsertTXs(block, iD => segments[iD[0]] != null);

        return segments.Select((s, i) => s == null ? null : SerializeSegment(i)).ToArray();
      }
      finally
      {
        LiteDatabase.Rollback();
        Drop();
      }
    }

    internal static bool IsSegmentValid(byte[] segment, int indexSegment)
    {
      List<Account> accounts;

      try
      {
        accounts = ParseAccounts(segment, segment.Length);
      }
      catch (Exception)
      {
        return false;
      }

      for (int i = 0; i < accounts.Count; i += 1)
        if (accounts[i].ID[0] != indexSegment || i > 0 && ComparerID.Compare(accounts[i - 1].ID, accounts[i].ID) >= 0)
          return false;

      return true;
    }

    static List<Account> ParseAccounts(byte[] buffer, int length)
    {
      int index = 0;

      int countAccounts = VarInt.GetInt(buffer, ref index);

      if (index + (long)countAccounts * Account.LENGTH_ACCOUNT != length)
        throw new ProtocolException($"Length {length} of DB does not match its {countAccounts} accounts.");

      List<Account> accounts = new();

      for (int i = 0; i < countAccounts; i += 1)
        accounts.Add(new()
        {
          ID = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH160),
          HeightAccountCreated = buffer.Read<int>(ref index),
          Nonce = buffer.Read<int>(ref index),
          Balance = buffer.Read<long>(ref index)
        });

      return accounts;
    }

    internal Account LoadAccount(byte[] iDAccount, int heightAccountCreated)
    {
      if (AccountsStored.FindById(iDAccount) is Account accountStored)
        return accountStored;

      return new()
      {
        ID = iDAccount,
        HeightAccountCreated = heightAccountCreated
      };
    }

    internal Account LoadAccountExisting(byte[] iDAccount)
    {
      if (AccountsStored.FindById(iDAccount) is Account accountStored)
        return accountStored;

      throw new ProtocolException($"Account {iDAccount.ToHexString()} not found in database.");
    }

    void WriteAccount(Account account)
    {
      if (!AccountsUndo.ContainsKey(account.ID))
        AccountsUndo.Add(account.ID, AccountsStored.FindById(account.ID) ?? new Account { ID = account.ID });

      AccountsStored.Upsert(account);
    }

    internal void InsertBlock(Block block)
    {
      InsertTXs(block, iD => true);
      VerifyHashAccounts((HeaderBToken)block.Header);
    }

    void InsertTXs(Block block, Func<byte[], bool> isAccountApplied)
    {
      AccountsUndo.Clear();

      for (int i = 0; i < block.TXs.Count; i += 1)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        foreach (TXOutput tXOutput in tX.TXOutputs)
          if (tXOutput is TXOutputP2PKH tXOutputP2PKH && tXOutputP2PKH.Value > 0 && isAccountApplied(tXOutputP2PKH.IDAccount))
            ChangeBalance(tXOutputP2PKH.IDAccount, block.Header.Height, tXOutputP2PKH.Value);

        if (i > 0 && isAccountApplied(tX.IDAccountSource))
        {
          Account accountSource = LoadAccountExisting(tX.IDAccountSource);
          accountSource.SpendTX(tX);
          WriteAccount(accountSource);
        }
      }

      foreach (byte[] iD in AccountsUndo.Keys)
        if (AccountsStored.FindById(iD).Balance == 0)
          AccountsStored.Delete(iD);

      UndoStored.InsertBulk(AccountsUndo.Values.Select(account => new BsonDocument
      {
        [NAME_FIELD_HEIGHT] = block.Header.Height,
        [NAME_FIELD_ACCOUNT] = LiteDatabase.Mapper.ToDocument(account)
      }));

      UndoStored.DeleteMany(Query.LT(NAME_FIELD_HEIGHT, block.Header.Height - COUNT_MAX_BLOCKS_STORED + 1));
    }

    internal void RollBack(int height)
    {
      foreach (Account account in LoadAccountsUndo(UndoStored.Query().Where(Query.EQ(NAME_FIELD_HEIGHT, height))).ToList())
        if (account.Balance > 0)
          AccountsStored.Upsert(account);
        else
          AccountsStored.Delete(account.ID);

      UndoStored.DeleteMany(Query.EQ(NAME_FIELD_HEIGHT, height));
    }

    IEnumerable<Account> LoadAccountsUndo(ILiteQueryable<BsonDocument> query)
    {
      return query.ToEnumerable().Select(d => LiteDatabase.Mapper.ToObject<Account>(d[NAME_FIELD_ACCOUNT].AsDocument));
    }

    void ChangeBalance(byte[] iDAccount, int heightAccountCreated, long valueChange)
    {
      Account account = LoadAccount(iDAccount, heightAccountCreated);
      account.Balance += valueChange;
      WriteAccount(account);
    }

    internal byte[] ComputeHash(Block block)
    {
      LiteDatabase.BeginTrans();

      try
      {
        InsertTXs(block, iD => true);
        return SHA256.HashData(SerializeSegment(block.Header.Height % COUNT_SEGMENTS));
      }
      finally
      {
        LiteDatabase.Rollback();
      }
    }

    void VerifyHashAccounts(HeaderBToken header)
    {
      int indexSegment = header.Height % COUNT_SEGMENTS;

      if (!SHA256.HashData(SerializeSegment(indexSegment)).IsAllBytesEqual(header.HashAccounts))
        throw new ProtocolException($"Hash of segment {indexSegment} not equal to HashAccounts of block {header.Hash.ToHexString()}.");
    }

    byte[] SerializeSegment(int indexSegment)
    {
      byte[] iDFirst = new byte[Crypto.LENGTH_HASH160];
      byte[] iDLast = Enumerable.Repeat((byte)0xFF, Crypto.LENGTH_HASH160).ToArray();

      iDFirst[0] = iDLast[0] = (byte)indexSegment;

      IEnumerable<Account> accountsSegment = AccountsStored.Query()
        .Where(Query.Between(NAME_FIELD_ID, iDFirst, iDLast))
        .OrderBy(NAME_FIELD_ID)
        .ToEnumerable();

      return SerializeAccounts(accountsSegment);
    }
  }
}
