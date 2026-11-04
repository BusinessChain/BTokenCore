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
        bytes.AddRange(BitConverter.GetBytes(account.HeightAccountCreated));
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
        accounts.Add(new()
        {
          ID = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH160),
          HeightAccountCreated = buffer.Read<int>(ref index),
          Nonce = buffer.Read<int>(ref index),
          Balance = buffer.Read<long>(ref index)
        });

      AccountsStored = LiteDatabase.GetCollection<Account>(NAME_COLLECTION);
      AccountsStored.InsertBulk(accounts);
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

    internal Account GetCopyOfAccount(byte[] iDAccount)
    {
      if (AccountsStored.FindById(iDAccount) is Account accountStored)
        return new(accountStored);
      else
        throw new ProtocolException($"Account {iDAccount.ToHexString()} not found in database.");
    }

    void WriteAccount(Account account)
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
        {
          Account accountSource = GetCopyOfAccount(tX.IDAccountSource);
          accountSource.SpendTX(tX);
          WriteAccount(accountSource);
        }
      }
    }

    internal void RollBackTXs(Block block)
    {
      for (int i = block.TXs.Count - 1; i >= 0; i--)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        if (i > 0)
        {
          Account accountSource = LoadAccount(tX.IDAccountSource, tX.HeightAccountCreated);
          accountSource.ReverseSpendTX(tX);
          WriteAccount(accountSource);
        }

        foreach (TXOutput tXOutput in tX.TXOutputs)
          if (tXOutput is TXOutputP2PKH tXOutputP2PKH && tXOutputP2PKH.Value > 0)
            ChangeBalance(tXOutputP2PKH.IDAccount, block.Header.Height, -tXOutputP2PKH.Value);
      }
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
        return SHA256.HashData(Serialize());
      }
      finally
      {
        LiteDatabase.Rollback();
      }
    }
  }
}
