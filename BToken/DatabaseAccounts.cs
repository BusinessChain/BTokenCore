using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class DatabaseAccounts
  {
    const string NAME_COLLECTION = "accounts";
    const string NAME_COLLECTION_UNDO = "accountsUndo";
    const string NAME_FIELD_ACCOUNTS = "accounts";

    LiteDatabase LiteDatabase;
    ILiteCollection<Account> AccountsStored;
    ILiteCollection<BsonDocument> UndoStored;
    Dictionary<byte[], Account> AccountsUndo = new(new EqualityComparerByteArray());

    static readonly Comparer<byte[]> ComparerID = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));


    internal bool TryLoad(LiteDatabase storage)
    {
      LiteDatabase = storage;

      bool isStored = LiteDatabase.CollectionExists(NAME_COLLECTION);
      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);
      UndoStored = LiteDatabase.GetCollection<BsonDocument>(NAME_COLLECTION_UNDO);

      return isStored;
    }

    internal void Drop()
    {
      LiteDatabase.DropCollection(NAME_COLLECTION);
      LiteDatabase.DropCollection(NAME_COLLECTION_UNDO);
      AccountsStored = null;
    }

    internal bool Exists()
    {
      return AccountsStored != null;
    }

    internal List<byte[]> SerializeSegmentsBelowWindow(int heightTip)
    {
      Dictionary<byte[], Account> accountsBelowWindow = new(new EqualityComparerByteArray());

      for (int height = heightTip; height > heightTip - COUNT_MAX_BLOCKS_STORED; height -= 1)
        foreach (Account account in LoadAccountsUndo(height))
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

    internal void Install(List<byte[]> segments)
    {
      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);

      foreach (byte[] segment in segments)
        AccountsStored.InsertBulk(ParseAccounts(segment, segment.Length));
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

      if (account.Balance > 0)
        AccountsStored.Upsert(account);
      else
        AccountsStored.Delete(account.ID);
    }

    internal void InsertBlock(Block block)
    {
      InsertTXs(block);
      VerifyHashAccounts((HeaderBToken)block.Header);
    }

    void InsertTXs(Block block)
    {
      AccountsUndo.Clear();

      for (int i = 0; i < block.TXs.Count; i += 1)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        foreach (TXOutput tXOutput in tX.TXOutputs)
          if (tXOutput is TXOutputP2PKH tXOutputP2PKH && tXOutputP2PKH.Value > 0)
            ChangeBalance(tXOutputP2PKH.IDAccount, block.Header.Height, tXOutputP2PKH.Value);

        if (i > 0)
        {
          Account accountSource = LoadAccountExisting(tX.IDAccountSource);
          accountSource.SpendTX(tX);
          WriteAccount(accountSource);
        }
      }

      UndoStored.Upsert(new BsonDocument
      {
        [NAME_FIELD_ID] = block.Header.Height,
        [NAME_FIELD_ACCOUNTS] = SerializeAccounts(AccountsUndo.Values)
      });

      UndoStored.DeleteMany(Query.LT(NAME_FIELD_ID, block.Header.Height - COUNT_MAX_BLOCKS_STORED + 1));
    }

    internal void RollBack(int height)
    {
      foreach (Account account in LoadAccountsUndo(height))
        WriteAccount(account);

      UndoStored.Delete(height);
    }

    List<Account> LoadAccountsUndo(int height)
    {
      if (UndoStored.FindById(height) is not BsonDocument documentUndo)
        throw new InvalidOperationException($"No undo data for block at height {height}.");

      byte[] accountsUndo = documentUndo[NAME_FIELD_ACCOUNTS].AsBinary;

      return ParseAccounts(accountsUndo, accountsUndo.Length);
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
        InsertTXs(block);
        return HashSegment(block.Header.Height);
      }
      finally
      {
        LiteDatabase.Rollback();
      }
    }

    void VerifyHashAccounts(HeaderBToken header)
    {
      if (!HashSegment(header.Height).IsAllBytesEqual(header.HashAccounts))
        throw new ProtocolException($"Hash of segment {header.Height % COUNT_SEGMENTS} not equal to HashAccounts of block {header.Hash.ToHexString()}.");
    }

    byte[] HashSegment(int height)
    {
      byte[] iDFirst = new byte[Crypto.LENGTH_HASH160];
      byte[] iDLast = Enumerable.Repeat((byte)0xFF, Crypto.LENGTH_HASH160).ToArray();

      iDFirst[0] = iDLast[0] = (byte)(height % COUNT_SEGMENTS);

      IEnumerable<Account> accountsSegment = AccountsStored.Query()
        .Where(Query.Between(NAME_FIELD_ID, iDFirst, iDLast))
        .OrderBy(NAME_FIELD_ID)
        .ToEnumerable();

      return SHA256.HashData(SerializeAccounts(accountsSegment));
    }
  }
}
