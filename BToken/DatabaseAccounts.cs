using LiteDB;
using System.Runtime.InteropServices;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  partial class DatabaseAccounts
  {
    const string NAME_COLLECTION = "accounts";
    const string NAME_COLLECTION_UNDO = "accountsUndo";
    const string NAME_COLLECTION_BLOCK_APPLIED_LAST = "accountsBlockAppliedLast";
    const string NAME_COLLECTION_HEIGHT_INSTALLED = "accountsHeightInstalled";
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
    Segment[] Segments;
    ILiteCollection<Account> AccountsStored;
    ILiteCollection<BsonDocument> UndoStored;
    ILiteCollection<BsonDocument> BlockAppliedLastStored;
    ILiteCollection<BsonDocument> HeightInstalledStored;
    ILiteCollection<BsonDocument> CountsAccountsPerSegmentStored;
    HashSet<int> IndicesSegmentTouched = new();
    int[] CountsAccountsPerSegment;
    long CountAccountsMax;


    internal void Load(LiteDatabase storage)
    {
      LiteDatabase = storage;
      UndoStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_UNDO);
      BlockAppliedLastStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_BLOCK_APPLIED_LAST);
      HeightInstalledStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_HEIGHT_INSTALLED);
      CountsAccountsPerSegmentStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_COUNTS);

      if (BlockAppliedLastStored.FindById(0) != null)
        OpenAccounts();
      else
        Drop();
    }

    void OpenAccounts()
    {
      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);
      UndoStored.EnsureIndex(NAME_FIELD_HEIGHT);
      UndoStored.EnsureIndex(PATH_ID_ACCOUNT_UNDO);

      Segments = new Segment[COUNT_SEGMENTS];

      for (int i = 0; i < COUNT_SEGMENTS; i += 1)
        Segments[i] = new Segment(LiteDatabase, i);
    }

    internal void Drop()
    {
      LiteDatabase.DropCollection(NAME_COLLECTION);
      LiteDatabase.DropCollection(NAME_COLLECTION_UNDO);
      LiteDatabase.DropCollection(NAME_COLLECTION_BLOCK_APPLIED_LAST);
      LiteDatabase.DropCollection(NAME_COLLECTION_HEIGHT_INSTALLED);
      LiteDatabase.DropCollection(NAME_COLLECTION_COUNTS);
      AccountsStored = null;
      Segments = null;
    }

    void LoadCountsAccountsPerSegment()
    {
      CountsAccountsPerSegment = CountsAccountsPerSegmentStored.FindById(0) is BsonDocument document
        ? MemoryMarshal.Cast<byte, int>(document[NAME_FIELD_COUNTS].AsBinary).ToArray()
        : new int[COUNT_SEGMENTS];
    }

    void StoreCountsAccountsPerSegment()
    {
      CountsAccountsPerSegmentStored.Upsert(new BsonDocument
      {
        [NAME_FIELD_ID] = 0,
        [NAME_FIELD_COUNTS] = MemoryMarshal.AsBytes(CountsAccountsPerSegment.AsSpan()).ToArray()
      });
    }

    int GetHeightBlockAppliedLast()
    {
      return BlockAppliedLastStored.FindById(0)?[NAME_FIELD_HEIGHT].AsInt32 ?? 0;
    }

    void SetBlockAppliedLast(int height, byte[] hashBlock)
    {
      BlockAppliedLastStored.Upsert(new BsonDocument { [NAME_FIELD_ID] = 0, [NAME_FIELD_HEIGHT] = height, [NAME_FIELD_HASH_BLOCK] = hashBlock });
    }

    internal byte[] GetHashBlockAppliedLast()
    {
      return BlockAppliedLastStored.FindById(0)[NAME_FIELD_HASH_BLOCK].AsBinary;
    }

    internal int GetHeightInstalled()
    {
      return HeightInstalledStored.FindById(0)?[NAME_FIELD_HEIGHT].AsInt32 ?? 0;
    }

    static long GetOffsetRent(int indexSegment, int height)
    {
      return RENT_SWEEP * ((height + COUNT_SEGMENTS - indexSegment) / COUNT_SEGMENTS);
    }

    internal bool Exists()
    {
      return Segments != null;
    }

    internal byte[] SerializeSegmentAtHeight(int height)
    {
      return Segments[height % COUNT_SEGMENTS].SerializeAtHeight(height);
    }

    internal void Install(IList<byte[]> segments, Header header)
    {
      OpenAccounts();
      SetBlockAppliedLast(header.Height, header.Hash);
      HeightInstalledStored.Upsert(new BsonDocument { [NAME_FIELD_ID] = 0, [NAME_FIELD_HEIGHT] = header.Height });

      CountsAccountsPerSegment = new int[COUNT_SEGMENTS];

      for (int i = 0; i < segments.Count; i += 1)
        if (segments[i] != null)
          CountsAccountsPerSegment[i] = Segments[i].Install(segments[i]);

      StoreCountsAccountsPerSegment();
    }

    internal void CarryBlock(Block block, int[] heightsSweep)
    {
      int height = block.Header.Height;

      InsertTXs(block, i => height > heightsSweep[i], isLimitChecked: false);

      if (heightsSweep[height % COUNT_SEGMENTS] < height)
        VerifyHashAccounts((HeaderBToken)block.Header);

      UndoStored.DeleteMany(Query.EQ(NAME_FIELD_HEIGHT, height));
    }

    internal Account LoadAccount(byte[] iDAccount, int heightAccountCreated)
    {
      return Segments[GetIndexSegment(iDAccount)].LoadAccount(iDAccount, GetHeightBlockAppliedLast()) ?? new()
      {
        ID = iDAccount,
        HeightAccountCreated = heightAccountCreated
      };
    }

    internal Account LoadAccountExisting(byte[] iDAccount)
    {
      return Segments[GetIndexSegment(iDAccount)].LoadAccount(iDAccount, GetHeightBlockAppliedLast())
        ?? throw new ProtocolException($"Account {iDAccount.ToHexString()} not found in database.");
    }

    internal Account LoadAccountNext(byte[] iDAccount)
    {
      Account account = Segments[GetIndexSegment(iDAccount)].LoadAccount(iDAccount, GetHeightBlockAppliedLast() + 1);

      return account?.Balance > 0 ? account : null;
    }

    internal bool IsInSweepNext(byte[] iDAccount)
    {
      return GetIndexSegment(iDAccount) == (GetHeightBlockAppliedLast() + 1) % COUNT_SEGMENTS;
    }

    void WriteAccount(Account account)
    {
      int indexSegment = GetIndexSegment(account.ID);

      if (IndicesSegmentTouched.Add(indexSegment))
        Segments[indexSegment].ClearUndo();

      Segments[indexSegment].WriteAccount(account, GetHeightBlockAppliedLast());
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

      foreach (Account account in Segments[indexSegment].LoadAccounts())
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
      IndicesSegmentTouched.Clear();

      int height = block.Header.Height;

      SetBlockAppliedLast(height, block.Header.Hash);
      LoadCountsAccountsPerSegment();

      List<Account> accountsSwept = new();
      long rent = 0;

      if (isSegmentApplied(height % COUNT_SEGMENTS))
      {
        (accountsSwept, rent) = LoadSweep(height);

        foreach (Account account in accountsSwept)
          AccountsStored.Delete(account.ID);

        CountsAccountsPerSegment[height % COUNT_SEGMENTS] -= accountsSwept.Count;
      }

      CountAccountsMax = isLimitChecked ? ComputeCountAccountsMax(CountsAccountsPerSegment) : long.MaxValue;

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

      foreach (int indexSegment in IndicesSegmentTouched)
        CountsAccountsPerSegment[indexSegment] -= Segments[indexSegment].FinishBlock(height);

      StoreCountsAccountsPerSegment();

      UndoStored.InsertBulk(accountsSwept.Select(a => Segments[height % COUNT_SEGMENTS].CreateDocumentUndo(a, height, isSweep: true)));

      UndoStored.DeleteMany(Query.LT(NAME_FIELD_HEIGHT, height - COUNT_MAX_BLOCKS_STORED + 1));

      return rent;
    }

    internal void RollBack(Header header)
    {
      int height = header.Height;

      IEnumerable<IGrouping<int, BsonDocument>> documentsUndoPerSegment = UndoStored.Query()
        .Where(Query.EQ(NAME_FIELD_HEIGHT, height))
        .OrderBy(NAME_FIELD_IS_SWEEP)
        .ToEnumerable()
        .GroupBy(d => GetIndexSegment(d[NAME_FIELD_ACCOUNT][NAME_FIELD_ID].AsBinary))
        .ToList();

      LoadCountsAccountsPerSegment();

      foreach (IGrouping<int, BsonDocument> documentsUndo in documentsUndoPerSegment)
        CountsAccountsPerSegment[documentsUndo.Key] += Segments[documentsUndo.Key].RollBack(documentsUndo);

      StoreCountsAccountsPerSegment();

      UndoStored.DeleteMany(Query.EQ(NAME_FIELD_HEIGHT, height));

      SetBlockAppliedLast(height - 1, header.HashPrevious);
    }

    void ChangeBalance(byte[] iDAccount, int heightAccountCreated, long valueChange)
    {
      Account account = Segments[GetIndexSegment(iDAccount)].LoadAccount(iDAccount, GetHeightBlockAppliedLast());

      if (account == null)
      {
        int indexSegment = GetIndexSegment(iDAccount);

        if (CountsAccountsPerSegment[indexSegment] + 1 > CountAccountsMax)
          throw new ProtocolException($"Account {iDAccount.ToHexString()} exceeds the limit of {CountAccountsMax} accounts in segment {indexSegment}.");

        CountsAccountsPerSegment[indexSegment] += 1;

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
        return Segments[block.Header.Height % COUNT_SEGMENTS].ComputeHash();
      }
      finally
      {
        LiteDatabase.Rollback();
      }
    }

    void VerifyHashAccounts(HeaderBToken header)
    {
      int indexSegment = header.Height % COUNT_SEGMENTS;

      if (!Segments[indexSegment].ComputeHash().IsAllBytesEqual(header.HashAccounts))
        throw new ProtocolException($"Hash of segment {indexSegment} not equal to HashAccounts of block {header.Hash.ToHexString()}.");
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

    internal (int[] countsAccount, long countAccountsMax) LoadCountsAccountsPerSegmentNext()
    {
      int heightNext = GetHeightBlockAppliedLast() + 1;

      LoadCountsAccountsPerSegment();

      CountsAccountsPerSegment[heightNext % COUNT_SEGMENTS] -= LoadSweep(heightNext).accountsSwept.Count;

      return (CountsAccountsPerSegment, ComputeCountAccountsMax(CountsAccountsPerSegment));
    }
  }
}
