using LiteDB;
using System.Security.Cryptography;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class DatabaseAccounts
  {
    const string NAME_COLLECTION = "accounts";

    LiteDatabase LiteDatabase;
    ILiteCollection<Account> AccountsStored;


    internal bool TryLoad(LiteDatabase storage)
    {
      LiteDatabase = storage;

      bool isStored = LiteDatabase.CollectionExists(NAME_COLLECTION);
      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);

      return isStored;
    }

    internal void Drop()
    {
      LiteDatabase.DropCollection(NAME_COLLECTION);
      AccountsStored = null;
    }

    internal bool Exists()
    {
      return AccountsStored != null;
    }

    internal byte[] Serialize()
    {
      List<Account> accounts = AccountsStored.Find(Query.All(Query.Ascending)).ToList();

      List<byte> bytes = new();

      bytes.AddRange(VarInt.GetBytes(accounts.Count));

      foreach (Account account in accounts)
      {
        bytes.AddRange(account.ID);
        bytes.AddRange(BitConverter.GetBytes(account.HeightBlockAccountCreated));
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

        account.ID = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH160);

        account.HeightBlockAccountCreated = buffer.Read<int>(ref index);

        account.Nonce = buffer.Read<int>(ref index);

        account.Balance = buffer.Read<long>(ref index);

        accounts.Add(account);
      }

      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);
      AccountsStored.InsertBulk(accounts);
    }

    internal Account LoadAccount(byte[] accountID, int blockHeightAccountCreated)
    {
      if (AccountsStored.FindById(accountID) is Account accountStored)
        return accountStored;

      return new()
      {
        ID = accountID,
        HeightBlockAccountCreated = blockHeightAccountCreated
      };
    }

    internal Account GetCopyOfAccount(byte[] accountID)
    {
      if (AccountsStored.FindById(accountID) is Account accountStored)
        return new(accountStored);
      else
        throw new ProtocolException($"Account {accountID.ToHexString()} not found in database.");
    }

    internal void WriteAccount(Account account)
    {
      if (account.Balance > 0)
        AccountsStored.Upsert(account);
      else
        AccountsStored.Delete(account.ID);
    }

    internal void InsertTXs(Block block)
    {
      for (int i = 0; i < block.TXs.Count; i += 1)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        foreach (TXOutput tXOutput in tX.TXOutputs)
          if (tXOutput is TXOutputP2PKH tXOutputP2PKH && tXOutputP2PKH.Value > 0)
            ChangeBalance(tXOutputP2PKH.IDAccount, block.Header.Height, tXOutputP2PKH.Value);

        if (i > 0)
          SpendTXInput(tX);
      }
    }

    internal void ChangeBalance(byte[] accountID, int blockHeightAccountCreated, long valueChange)
    {
      Account account = LoadAccount(accountID, blockHeightAccountCreated);
      account.Balance += valueChange;
      WriteAccount(account);
    }

    void SpendTXInput(TXBToken tX)
    {
      Account account = GetCopyOfAccount(tX.IDAccountSource);
      account.SpendTX(tX);
      WriteAccount(account);
    }

    internal byte[] ComputeHash(Block block)
    {
      LiteDatabase.BeginTrans();

      try
      {
        InsertTXs(block);
        return SHA256.HashData(Serialize());
      }
      finally
      {
        LiteDatabase.Rollback();
      }
    }
  }
}
