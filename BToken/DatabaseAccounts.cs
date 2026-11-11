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

    LiteDatabase LiteDatabase;
    Segment[] Segments;
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

    internal bool Exists()
    {
      return Segments != null;
    }

    internal byte[] SerializeSegmentAtHeight(int height)
    {
      return Segments[height % COUNT_SEGMENTS].SerializeAtHeight(height);
    }

    internal static bool IsSegmentValid(byte[] segment, int indexSegment)
    {
      return Segment.IsSegmentValid(segment, indexSegment);
    }

    internal void Install(IList<byte[]> segments, Header header, int[] heightsSweep, Func<Header, Block> loadBlock, out Header headerFailed)
    {
      headerFailed = null;

      List<Header> headersCarried = new();

      for (Header headerCarried = header; headerCarried.Height > heightsSweep.DefaultIfEmpty(header.Height).Min(); headerCarried = headerCarried.HeaderPrevious)
        headersCarried.Insert(0, headerCarried);

      OpenAccounts();
      SetBlockAppliedLast(header.Height, header.Hash);
      HeightInstalledStored.Upsert(new BsonDocument { [NAME_FIELD_ID] = 0, [NAME_FIELD_HEIGHT] = header.Height });

      CountsAccountsPerSegment = new int[COUNT_SEGMENTS];

      for (int i = 0; i < segments.Count; i += 1)
        if (segments[i] != null)
          CountsAccountsPerSegment[i] = Segments[i].Install(segments[i]);

      StoreCountsAccountsPerSegment();

      foreach (Header headerCarried in headersCarried)
      {
        headerFailed = headerCarried;
        int height = headerCarried.Height;

        InsertTXs(loadBlock(headerCarried), i => height > heightsSweep[i], isLimitChecked: false);

        if (heightsSweep[height % COUNT_SEGMENTS] < height)
          VerifyHashAccounts((HeaderBToken)headerCarried);

        UndoStored.DeleteMany(Query.EQ(NAME_FIELD_HEIGHT, height));
      }
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
      TouchSegment(GetIndexSegment(account.ID)).WriteAccount(account, GetHeightBlockAppliedLast());
    }

    Segment TouchSegment(int indexSegment)
    {
      if (IndicesSegmentTouched.Add(indexSegment))
        Segments[indexSegment].ClearUndo();

      return Segments[indexSegment];
    }

    internal void InsertBlock(Block block)
    {
      long rent = InsertTXs(block, i => true, isLimitChecked: true);
      VerifyCoinbase(block, rent);
      VerifyHashAccounts((HeaderBToken)block.Header);
    }

    internal long LoadRentSweep(int height)
    {
      return Segments[height % COUNT_SEGMENTS].ComputeSweep(height).rent;
    }

    long InsertTXs(Block block, Func<int, bool> isSegmentApplied, bool isLimitChecked)
    {
      IndicesSegmentTouched.Clear();

      int height = block.Header.Height;

      SetBlockAppliedLast(height, block.Header.Hash);
      LoadCountsAccountsPerSegment();

      int indexSegmentSweep = height % COUNT_SEGMENTS;
      long rent = 0;

      if (isSegmentApplied(indexSegmentSweep))
      {
        (int countAccountsSwept, rent) = TouchSegment(indexSegmentSweep).Sweep(height);
        CountsAccountsPerSegment[indexSegmentSweep] -= countAccountsSwept;
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

      CountsAccountsPerSegment[heightNext % COUNT_SEGMENTS] -= Segments[heightNext % COUNT_SEGMENTS].ComputeSweep(heightNext).countAccountsSwept;

      return (CountsAccountsPerSegment, ComputeCountAccountsMax(CountsAccountsPerSegment));
    }
  }
}
