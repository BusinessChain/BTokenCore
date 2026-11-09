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
    const string NAME_COLLECTION_HEIGHT = "accountsHeight";
    const string NAME_FIELD_ACCOUNT = "account";
    const string NAME_FIELD_IS_SWEEP = "isSweep";
    const string PATH_ID_ACCOUNT_UNDO = "$." + NAME_FIELD_ACCOUNT + "." + NAME_FIELD_ID;
    const int SHIFT_INDEX_SEGMENT = 16 - COUNT_BITS_INDEX_SEGMENT;
    const long RENT_PER_BYTE = 1;
    const long RENT_SWEEP = RENT_PER_BYTE * Account.LENGTH_ACCOUNT;

    LiteDatabase LiteDatabase;
    ILiteCollection<Account> AccountsStored;
    ILiteCollection<BsonDocument> UndoStored;
    ILiteCollection<BsonDocument> HeightStored;
    Dictionary<byte[], Account> AccountsUndo = new(new EqualityComparerByteArray());

    static readonly Comparer<byte[]> ComparerID = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));


    internal bool TryLoad(LiteDatabase storage)
    {
      LiteDatabase = storage;
      UndoStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_UNDO);
      HeightStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_HEIGHT);

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
      LiteDatabase.DropCollection(NAME_COLLECTION_HEIGHT);
      AccountsStored = null;
    }

    int GetHeight()
    {
      return HeightStored.FindById(0)?[NAME_FIELD_HEIGHT].AsInt32 ?? 0;
    }

    void SetHeight(int height)
    {
      HeightStored.Upsert(new BsonDocument { [NAME_FIELD_ID] = 0, [NAME_FIELD_HEIGHT] = height });
    }

    static long GetOffsetRent(int indexSegment, int height)
    {
      return RENT_SWEEP * ((height + COUNT_SEGMENTS - indexSegment) / COUNT_SEGMENTS);
    }

    internal bool Exists()
    {
      return AccountsStored != null;
    }

    internal byte[] SerializeSegmentBelowWindow(int indexSegment)
    {
      Dictionary<byte[], Account> accountsBelowWindow = new(new EqualityComparerByteArray());

      IEnumerable<BsonDocument> documentsUndo = UndoStored.Query()
        .Where(QueryRangeSegment(PATH_ID_ACCOUNT_UNDO, indexSegment))
        .ToEnumerable()
        .OrderByDescending(d => d[NAME_FIELD_HEIGHT].AsInt32)
        .ThenBy(d => d[NAME_FIELD_IS_SWEEP].AsBoolean);

      foreach (Account account in LoadAccountsUndo(documentsUndo))
        accountsBelowWindow[account.ID] = account;

      IEnumerable<Account> accountsStoredUnchanged = LoadAccountsSegment(indexSegment)
        .Where(a => !accountsBelowWindow.ContainsKey(a.ID));

      List<Account> accountsUndoExisting = accountsBelowWindow.Values
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

    internal void Install(IEnumerable<byte[]> segments, int height)
    {
      OpenAccounts();
      SetHeight(height);

      foreach (byte[] segment in segments.Where(s => s != null))
        AccountsStored.InsertBulk(ParseAccounts(segment, segment.Length));
    }

    internal byte[][] AdvanceSegments(byte[][] segments, int height, List<Block> blocks)
    {
      LiteDatabase.BeginTrans();

      try
      {
        Install(segments, height);

        foreach (Block block in blocks)
          InsertTXs(block, i => segments[i] != null);

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
        if (GetIndexSegment(accounts[i].ID) != indexSegment ||i > 0 && ComparerID.Compare(accounts[i - 1].ID, accounts[i].ID) >= 0)
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
      return LoadAccountStored(iDAccount) ?? new()
      {
        ID = iDAccount,
        HeightAccountCreated = heightAccountCreated
      };
    }

    internal Account LoadAccountExisting(byte[] iDAccount)
    {
      return LoadAccountStored(iDAccount)
        ?? throw new ProtocolException($"Account {iDAccount.ToHexString()} not found in database.");
    }

    Account LoadAccountStored(byte[] iDAccount)
    {
      Account account = AccountsStored.FindById(iDAccount);

      if (account != null)
        account.Balance -= GetOffsetRent(GetIndexSegment(iDAccount), GetHeight());

      return account;
    }

    void WriteAccount(Account account)
    {
      if (!AccountsUndo.ContainsKey(account.ID))
        AccountsUndo.Add(account.ID, AccountsStored.FindById(account.ID) ?? new Account { ID = account.ID });

      AccountsStored.Upsert(new Account
      {
        ID = account.ID,
        HeightAccountCreated = account.HeightAccountCreated,
        Nonce = account.Nonce,
        Balance = account.Balance + GetOffsetRent(GetIndexSegment(account.ID), GetHeight())
      });
    }

    internal void InsertBlock(Block block)
    {
      InsertTXs(block, i => true);
      VerifyHashAccounts((HeaderBToken)block.Header);
    }

    void InsertTXs(Block block, Func<int, bool> isSegmentApplied)
    {
      AccountsUndo.Clear();

      int height = block.Header.Height;
      int indexSegmentSweep = height % COUNT_SEGMENTS;

      SetHeight(height);

      List<Account> accountsSwept = new();

      if (isSegmentApplied(indexSegmentSweep))
      {
        long offsetRent = GetOffsetRent(indexSegmentSweep, height);

        accountsSwept = LoadAccountsSegment(indexSegmentSweep).Where(a => a.Balance <= offsetRent).ToList();

        foreach (Account account in accountsSwept)
          AccountsStored.Delete(account.ID);
      }

      for (int i = 0; i < block.TXs.Count; i += 1)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        foreach (TXOutput tXOutput in tX.TXOutputs)
          if (tXOutput is TXOutputP2PKH tXOutputP2PKH && tXOutputP2PKH.Value > 0 && isSegmentApplied(GetIndexSegment(tXOutputP2PKH.IDAccount)))
            ChangeBalance(tXOutputP2PKH.IDAccount, height, tXOutputP2PKH.Value);

        if (i > 0 && isSegmentApplied(GetIndexSegment(tX.IDAccountSource)))
        {
          Account accountSource = LoadAccountExisting(tX.IDAccountSource);
          accountSource.SpendTX(tX);
          WriteAccount(accountSource);
        }
      }

      foreach (byte[] iD in AccountsUndo.Keys)
        if (LoadAccountStored(iD).Balance == 0)
          AccountsStored.Delete(iD);

      UndoStored.InsertBulk(
        AccountsUndo.Values.Select(a => CreateDocumentUndo(a, height, isSweep: false))
        .Concat(accountsSwept.Select(a => CreateDocumentUndo(a, height, isSweep: true))));

      UndoStored.DeleteMany(Query.LT(NAME_FIELD_HEIGHT, height - COUNT_MAX_BLOCKS_STORED + 1));
    }

    BsonDocument CreateDocumentUndo(Account account, int height, bool isSweep)
    {
      return new BsonDocument
      {
        [NAME_FIELD_HEIGHT] = height,
        [NAME_FIELD_IS_SWEEP] = isSweep,
        [NAME_FIELD_ACCOUNT] = LiteDatabase.Mapper.ToDocument(account)
      };
    }

    internal void RollBack(int height)
    {
      IEnumerable<BsonDocument> documentsUndo = UndoStored.Query()
        .Where(Query.EQ(NAME_FIELD_HEIGHT, height))
        .OrderBy(NAME_FIELD_IS_SWEEP)
        .ToEnumerable();

      foreach (Account account in LoadAccountsUndo(documentsUndo).ToList())
        if (account.Balance > 0)
          AccountsStored.Upsert(account);
        else
          AccountsStored.Delete(account.ID);

      UndoStored.DeleteMany(Query.EQ(NAME_FIELD_HEIGHT, height));

      SetHeight(height - 1);
    }

    IEnumerable<Account> LoadAccountsUndo(IEnumerable<BsonDocument> documents)
    {
      return documents.Select(d => LiteDatabase.Mapper.ToObject<Account>(d[NAME_FIELD_ACCOUNT].AsDocument));
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
        InsertTXs(block, i => true);
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
      return SerializeAccounts(LoadAccountsSegment(indexSegment));
    }

    IEnumerable<Account> LoadAccountsSegment(int indexSegment)
    {
      return AccountsStored.Query()
        .Where(QueryRangeSegment(NAME_FIELD_ID, indexSegment))
        .OrderBy(NAME_FIELD_ID)
        .ToEnumerable();
    }

    static BsonExpression QueryRangeSegment(string pathID, int indexSegment)
    {
      byte[] iDFirst = new byte[Crypto.LENGTH_HASH160];
      byte[] iDLast = Enumerable.Repeat((byte)0xFF, Crypto.LENGTH_HASH160).ToArray();

      int prefixFirst = indexSegment << SHIFT_INDEX_SEGMENT;
      int prefixLast = prefixFirst | (1 << SHIFT_INDEX_SEGMENT) - 1;

      iDFirst[0] = (byte)(prefixFirst >> 8);
      iDFirst[1] = (byte)prefixFirst;
      iDLast[0] = (byte)(prefixLast >> 8);
      iDLast[1] = (byte)prefixLast;

      return Query.Between(pathID, iDFirst, iDLast);
    }

    static int GetIndexSegment(byte[] iD)
    {
      return (iD[0] << 8 | iD[1]) >> SHIFT_INDEX_SEGMENT;
    }
  }
}
