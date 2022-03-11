using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

using BTokenLib;

namespace BTokenCore
{
  class DatabaseAccounts
  {
    const ulong BLOCK_REWARD_INITIAL = 100000000000000; // 100 BTK
    const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

    const int COUNT_CACHES = 10;
    int IndexCache;
    List<Dictionary<byte[], RecordDatabaseAccounts>> Caches = new();

    string PathRootDB = "FilesDB";
    const int COUNT_FILES_DB = 1 << 16;
    FileStream[] FilesDB;

    const int LENGTH_RECORD_DB = 44;
    const int LENGTH_ID_ACCOUNT = 32;



    public DatabaseAccounts()
    {
      for (int i = 0; i < COUNT_CACHES; i += 1)
        Caches.Add(new Dictionary<byte[], RecordDatabaseAccounts>(
            new EqualityComparerByteArray()));


      Directory.CreateDirectory(PathRootDB);

      FilesDB = new FileStream[COUNT_FILES_DB];
      for (int i = 0; i < COUNT_FILES_DB; i += 1)
        FilesDB[i] = new FileStream(
          Path.Combine(PathRootDB, i.ToString()),
          FileMode.Append,
          FileAccess.ReadWrite,
          FileShare.ReadWrite);
    }

    public void InsertBlock(Block block)
    {
      List<TX> tXs = block.TXs;

      ulong feeBlock = 0;

      for (int t = 1; t < tXs.Count; t++)
      {
        byte[] iDAccount = tXs[t].TXInputs[0].TXIDOutput;

        feeBlock += tXs[t].Fee;

        int c = IndexCache;
        while (true)
        {
          if (Caches[c].TryGetValue(iDAccount, out RecordDatabaseAccounts accountInput))
          {
            SpendAccount(tXs[t], accountInput);

            if (accountInput.Value == 0)
              Caches[c].Remove(iDAccount);
            else if (c != IndexCache)
            {
              Caches[c].Remove(iDAccount);
              Caches[IndexCache].Add(iDAccount, accountInput);
            }

            break;
          }

          c = (c + Caches.Count - 1) % 10;

          if (c == IndexCache)
            if (TrySpendAccountInFilesDB(tXs[t], iDAccount))
              break;
            else
              throw new ProtocolException(
                $"Account {iDAccount.ToHexString()} referenced by TX\n" +
                $"{tXs[t].Hash.ToHexString()} not found in database.");
        }

        InsertOutputs(tXs[t].TXOutputs);
      }

      ulong outputValueTXCoinbase = 0;
      tXs[0].TXOutputs.ForEach(o => outputValueTXCoinbase += o.Value);

      ulong blockReward = BLOCK_REWARD_INITIAL >> 
        block.Header.Height / PERIOD_HALVENING_BLOCK_REWARD;

      if (blockReward + feeBlock != outputValueTXCoinbase)
        throw new ProtocolException(
          $"Output value of Coinbase TX {tXs[0].Hash.ToHexString()}\n" +
          $"does not add up to block reward {blockReward} plus block fee {feeBlock}.");

      InsertOutputs(tXs[0].TXOutputs);
    }

    bool TrySpendAccountInFilesDB(TX tX, byte[] iDAccount)
    {
      ushort keyFileDB = BitConverter.ToUInt16(iDAccount, 0);

      FileStream fileDB = FilesDB[keyFileDB];
      fileDB.Position = 0;

      while(fileDB.Position < fileDB.Length)
      {
        int i = 0;
        while(fileDB.ReadByte() == iDAccount[i++])
        {
          if(i == LENGTH_ID_ACCOUNT)
          {
            byte[] countdownToReplay = new byte[4];
            fileDB.Read(countdownToReplay);

            byte[] value = new byte[8];
            fileDB.Read(value);

            RecordDatabaseAccounts account = new()
            {
              IDAccount = iDAccount,
              CountdownToReplay = BitConverter.ToUInt32(value),
              Value = BitConverter.ToUInt64(value)
            };

            SpendAccount(tX, account);

            fileDB.Position -= LENGTH_RECORD_DB;
            fileDB.Write(new byte[LENGTH_RECORD_DB]);

            Caches[IndexCache].Add(iDAccount, account);

            return true;
          }
        }

        fileDB.Position += LENGTH_RECORD_DB - fileDB.Position % LENGTH_RECORD_DB;
      }

      return false;
    }

    void SpendAccount(TX tX, RecordDatabaseAccounts accountInput)
    {
      ulong valueSpend = tX.Fee;
      tX.TXOutputs.ForEach(o => valueSpend += o.Value);

      if (accountInput.CountdownToReplay != ((TXBToken)tX).CountdownToReplay)
        throw new ProtocolException(
          $"Account {accountInput.IDAccount.ToHexString()} referenced by TX\n" +
          $"{tX.Hash.ToHexString()} has unequal CountdownToReplay.");

      if (accountInput.Value < valueSpend)
        throw new ProtocolException(
          $"Account {accountInput.IDAccount.ToHexString()} referenced by TX\n" +
          $"{tX.Hash.ToHexString()} does not have enough fund.");

      accountInput.CountdownToReplay -= 1;
      accountInput.Value -= valueSpend;
    }

    void InsertOutputs(List<TXOutput> tXOutputs)
    {
      for (int i = 0; i < tXOutputs.Count; i++)
      {
        byte[] iDAccount = tXOutputs[i].Buffer;
        ulong outputValueTX = tXOutputs[i].Value;

        int c = IndexCache;

        while (true)
        {
          if (Caches[c].TryGetValue(
            iDAccount,
            out RecordDatabaseAccounts accountOutputExisting))
          {
            accountOutputExisting.Value += outputValueTX;

            if (c != IndexCache)
            {
              Caches[c].Remove(iDAccount);
              Caches[IndexCache].Add(iDAccount, accountOutputExisting);
            }

            break;
          }

          c = (c + Caches.Count - 1) % Caches.Count;

          if (c == IndexCache)
          {
            Caches[IndexCache].Add(iDAccount, new RecordDatabaseAccounts
            {
              CountdownToReplay = uint.MaxValue,
              Value = outputValueTX,
              IDAccount = iDAccount
            });

            break;
          }
        }
      }
    }
  }
}
