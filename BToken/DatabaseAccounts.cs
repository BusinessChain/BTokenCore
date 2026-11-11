using LiteDB;
using System.Runtime.InteropServices;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class DatabaseAccounts
  {
    const string NAME_COLLECTION = "accounts";
    const string NAME_COLLECTION_UNDO = "accountsUndo";
    const string NAME_COLLECTION_HEIGHT = "accountsHeight";
    const string NAME_COLLECTION_COUNTS = "accountsCounts";
    const string NAME_FIELD_ACCOUNT = "account";
    const string NAME_FIELD_COUNTS = "counts";
    const string NAME_FIELD_HASH_BLOCK = "hashBlock";
    const string NAME_FIELD_IS_SWEEP = "isSweep";
    const string PATH_ID_ACCOUNT_UNDO = "$." + NAME_FIELD_ACCOUNT + "." + NAME_FIELD_ID;
    const int SHIFT_INDEX_SEGMENT = 16 - COUNT_BITS_INDEX_SEGMENT;
    const long RENT_PER_BYTE = 1;
    const long RENT_SWEEP = RENT_PER_BYTE * Account.LENGTH_ACCOUNT;

    LiteDatabase LiteDatabase;
    ILiteCollection<Account> AccountsStored;
    ILiteCollection<BsonDocument> UndoStored;
    ILiteCollection<BsonDocument> HeightStored;
    ILiteCollection<BsonDocument> CountsStored;
    Dictionary<byte[], Account> AccountsUndo = new(new EqualityComparerByteArray());
    int[] CountsAccount;
    long CountAccountsMax;

    static readonly Comparer<byte[]> ComparerID = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));


    internal void Load(LiteDatabase storage)
    {
      LiteDatabase = storage;
      UndoStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_UNDO);
      HeightStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_HEIGHT);
      CountsStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_COUNTS);

      if (HeightStored.FindById(0) != null)
        OpenAccounts();
      else
        Drop();
    }

    void OpenAccounts()
    {
      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);
      UndoStored.EnsureIndex(NAME_FIELD_HEIGHT);
      UndoStored.EnsureIndex(PATH_ID_ACCOUNT_UNDO);
    }

    internal void Drop()
    {
      LiteDatabase.DropCollection(NAME_COLLECTION);
      LiteDatabase.DropCollection(NAME_COLLECTION_UNDO);
      LiteDatabase.DropCollection(NAME_COLLECTION_HEIGHT);
      LiteDatabase.DropCollection(NAME_COLLECTION_COUNTS);
      AccountsStored = null;
    }

    void LoadCounts()
    {
      CountsAccount = CountsStored.FindById(0) is BsonDocument document
        ? MemoryMarshal.Cast<byte, int>(document[NAME_FIELD_COUNTS].AsBinary).ToArray()
        : new int[COUNT_SEGMENTS];
    }

    void StoreCounts()
    {
      CountsStored.Upsert(new BsonDocument
      {
        [NAME_FIELD_ID] = 0,
        [NAME_FIELD_COUNTS] = MemoryMarshal.AsBytes(CountsAccount.AsSpan()).ToArray()
      });
    }

    int GetHeight()
    {
      return HeightStored.FindById(0)?[NAME_FIELD_HEIGHT].AsInt32 ?? 0;
    }

    void SetHeight(int height, byte[] hashBlock)
    {
      HeightStored.Upsert(new BsonDocument { [NAME_FIELD_ID] = 0, [NAME_FIELD_HEIGHT] = height, [NAME_FIELD_HASH_BLOCK] = hashBlock });
    }

    internal byte[] GetHashBlock()
    {
      return HeightStored.FindById(0)[NAME_FIELD_HASH_BLOCK].AsBinary;
    }

    internal int GetHeightInstalled()
    {
      return HeightStored.FindById(1)?[NAME_FIELD_HEIGHT].AsInt32 ?? 0;
    }

    static long GetOffsetRent(int indexSegment, int height)
    {
      return RENT_SWEEP * ((height + COUNT_SEGMENTS - indexSegment) / COUNT_SEGMENTS);
    }

    internal bool Exists()
    {
      return AccountsStored != null;
    }

    internal byte[] SerializeSegmentAtHeight(int indexSegment, int height)
    {
      Dictionary<byte[], Account> accountsAtHeight = new(new EqualityComparerByteArray());

      IEnumerable<BsonDocument> documentsUndo = UndoStored.Query()
        .Where(QueryRangeSegment(PATH_ID_ACCOUNT_UNDO, indexSegment))
        .Where(Query.GT(NAME_FIELD_HEIGHT, height))
        .ToEnumerable()
        .OrderByDescending(d => d[NAME_FIELD_HEIGHT].AsInt32)
        .ThenBy(d => d[NAME_FIELD_IS_SWEEP].AsBoolean);

      foreach (Account account in LoadAccountsUndo(documentsUndo))
        accountsAtHeight[account.ID] = account;

      IEnumerable<Account> accountsStoredUnchanged = LoadAccountsSegment(indexSegment)
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

    internal void Install(IList<byte[]> segments, Header header)
    {
      OpenAccounts();
      SetHeight(header.Height, header.Hash);
      HeightStored.Upsert(new BsonDocument { [NAME_FIELD_ID] = 1, [NAME_FIELD_HEIGHT] = header.Height });

      CountsAccount = new int[COUNT_SEGMENTS];

      for (int i = 0; i < segments.Count; i += 1)
        if (segments[i] != null)
        {
          List<Account> accounts = ParseAccounts(segments[i], segments[i].Length);
          AccountsStored.InsertBulk(accounts);
          CountsAccount[i] = accounts.Count;
        }

      StoreCounts();
    }

    internal void CarryBlock(Block block, int[] heightsSweep)
    {
      int height = block.Header.Height;

      InsertTXs(block, i => height > heightsSweep[i], isLimitChecked: false);

      if (heightsSweep[height % COUNT_SEGMENTS] < height)
        VerifyHashAccounts((HeaderBToken)block.Header);

      UndoStored.DeleteMany(Query.EQ(NAME_FIELD_HEIGHT, height));
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
      return LoadAccountStored(iDAccount, GetHeight()) ?? new()
      {
        ID = iDAccount,
        HeightAccountCreated = heightAccountCreated
      };
    }

    internal Account LoadAccountExisting(byte[] iDAccount)
    {
      return LoadAccountStored(iDAccount, GetHeight())
        ?? throw new ProtocolException($"Account {iDAccount.ToHexString()} not found in database.");
    }

    internal Account LoadAccountNext(byte[] iDAccount)
    {
      Account account = LoadAccountStored(iDAccount, GetHeight() + 1);

      return account?.Balance > 0 ? account : null;
    }

    internal bool IsInSweepNext(byte[] iDAccount)
    {
      return GetIndexSegment(iDAccount) == (GetHeight() + 1) % COUNT_SEGMENTS;
    }

    Account LoadAccountStored(byte[] iDAccount, int height)
    {
      Account account = AccountsStored.FindById(iDAccount);

      if (account != null)
        account.Balance -= GetOffsetRent(GetIndexSegment(iDAccount), height);

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
      long rent = InsertTXs(block, i => true, isLimitChecked: true);
      VerifyCoinbase(block, rent);
      VerifyHashAccounts((HeaderBToken)block.Header);
    }

    internal (List<Account> accountsSwept, long rent) LoadSweep(int height)
    {
      int indexSegment = height % COUNT_SEGMENTS;
      long offsetRent = GetOffsetRent(indexSegment, height);

      List<Account> accountsSwept = new();
      long rent = 0;

      foreach (Account account in LoadAccountsSegment(indexSegment))
        if (account.Balance <= offsetRent)
        {
          accountsSwept.Add(account);
          rent += account.Balance - offsetRent + RENT_SWEEP;
        }
        else
          rent += RENT_SWEEP;

      return (accountsSwept, rent);
    }

    long InsertTXs(Block block, Func<int, bool> isSegmentApplied, bool isLimitChecked)
    {
      AccountsUndo.Clear();

      int height = block.Header.Height;

      SetHeight(height, block.Header.Hash);
      LoadCounts();

      List<Account> accountsSwept = new();
      long rent = 0;

      if (isSegmentApplied(height % COUNT_SEGMENTS))
      {
        (accountsSwept, rent) = LoadSweep(height);

        foreach (Account account in accountsSwept)
          AccountsStored.Delete(account.ID);

        CountsAccount[height % COUNT_SEGMENTS] -= accountsSwept.Count;
      }

      CountAccountsMax = isLimitChecked ? ComputeCountAccountsMax(CountsAccount) : long.MaxValue;

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
        if (LoadAccountStored(iD, height).Balance == 0)
        {
          AccountsStored.Delete(iD);
          CountsAccount[GetIndexSegment(iD)] -= 1;
        }

      StoreCounts();

      UndoStored.InsertBulk(
        AccountsUndo.Values.Select(a => CreateDocumentUndo(a, height, isSweep: false))
        .Concat(accountsSwept.Select(a => CreateDocumentUndo(a, height, isSweep: true))));

      UndoStored.DeleteMany(Query.LT(NAME_FIELD_HEIGHT, height - COUNT_MAX_BLOCKS_STORED + 1));

      return rent;
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

    internal void RollBack(Header header)
    {
      int height = header.Height;

      IEnumerable<BsonDocument> documentsUndo = UndoStored.Query()
        .Where(Query.EQ(NAME_FIELD_HEIGHT, height))
        .OrderBy(NAME_FIELD_IS_SWEEP)
        .ToEnumerable();

      LoadCounts();

      foreach (Account account in LoadAccountsUndo(documentsUndo).ToList())
      {
        bool isAccountStored = AccountsStored.FindById(account.ID) != null;

        if (account.Balance > 0)
        {
          if (!isAccountStored)
            CountsAccount[GetIndexSegment(account.ID)] += 1;

          AccountsStored.Upsert(account);
        }
        else if (isAccountStored)
        {
          CountsAccount[GetIndexSegment(account.ID)] -= 1;
          AccountsStored.Delete(account.ID);
        }
      }

      StoreCounts();

      UndoStored.DeleteMany(Query.EQ(NAME_FIELD_HEIGHT, height));

      SetHeight(height - 1, header.HashPrevious);
    }

    IEnumerable<Account> LoadAccountsUndo(IEnumerable<BsonDocument> documents)
    {
      return documents.Select(d => LiteDatabase.Mapper.ToObject<Account>(d[NAME_FIELD_ACCOUNT].AsDocument));
    }

    void ChangeBalance(byte[] iDAccount, int heightAccountCreated, long valueChange)
    {
      Account account = LoadAccountStored(iDAccount, GetHeight());

      if (account == null)
      {
        int indexSegment = GetIndexSegment(iDAccount);

        if (CountsAccount[indexSegment] + 1 > CountAccountsMax)
          throw new ProtocolException($"Account {iDAccount.ToHexString()} exceeds the limit of {CountAccountsMax} accounts in segment {indexSegment}.");

        CountsAccount[indexSegment] += 1;

        account = new()
        {
          ID = iDAccount,
          HeightAccountCreated = heightAccountCreated
        };
      }

      account.Balance += valueChange;
      WriteAccount(account);
    }

    internal byte[] ComputeHash(Block block)
    {
      LiteDatabase.BeginTrans();

      try
      {
        InsertTXs(block, i => true, isLimitChecked: true);
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

    internal static int GetIndexSegment(byte[] iD)
    {
      return (iD[0] << 8 | iD[1]) >> SHIFT_INDEX_SEGMENT;
    }

    static long ComputeCountAccountsMax(int[] countsAccount)
    {
      long countAccountsTotal = countsAccount.Sum(c => (long)c);

      return Math.Max(countAccountsTotal * 5 / (4 * COUNT_SEGMENTS), countAccountsTotal / COUNT_SEGMENTS + 100);
    }

    internal (int[] countsAccount, long countAccountsMax) LoadCountsNext()
    {
      int heightNext = GetHeight() + 1;

      LoadCounts();

      CountsAccount[heightNext % COUNT_SEGMENTS] -= LoadSweep(heightNext).accountsSwept.Count;

      return (CountsAccount, ComputeCountAccountsMax(CountsAccount));
    }
  }
}
