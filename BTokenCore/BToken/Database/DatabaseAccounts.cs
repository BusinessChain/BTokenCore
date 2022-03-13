using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

using BTokenLib;

namespace BTokenCore
{
  partial class DatabaseAccounts
  {
    const ulong BLOCK_REWARD_INITIAL = 100000000000000; // 100 BTK
    const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

    const int COUNT_CACHES = 10;
    int IndexCache;
    const int COUNT_MAX_CACHE_INDEXED = 2000000; // Read from configuration file
    List<Dictionary<byte[], RecordDBAccounts>> Caches = new();

    const string PathRootDB = "FilesDB";
    const int COUNT_FILES_DB = 1 << 16;
    FileDB[] FilesDB;

    const int LENGTH_RECORD_DB = 44;
    const int LENGTH_ID_ACCOUNT = 32;
    const int LENGTH_COUNTDOWN_TO_REPLAY = 4;
    const int LENGTH_VALUE = 8;



    public DatabaseAccounts()
    {
      for (int i = 0; i < COUNT_CACHES; i += 1)
        Caches.Add(new Dictionary<byte[], RecordDBAccounts>(
            new EqualityComparerByteArray()));

      Directory.CreateDirectory(PathRootDB);

      FilesDB = new FileDB[COUNT_FILES_DB];

      for (int i = 0; i < COUNT_FILES_DB; i += 1)
        FilesDB[i] = new FileDB(Path.Combine(PathRootDB, i.ToString()));
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
          if (Caches[c].TryGetValue(iDAccount, out RecordDBAccounts account))
          {
            SpendAccount(tXs[t], account);

            if (account.Value == 0)
              Caches[c].Remove(iDAccount);

            break;
          }

          c = (c + Caches.Count - 1) % 10;

          if (c == IndexCache)
          {
            GetFileDB(iDAccount).SpendAccountInDB(iDAccount, tXs[t]);
            break;
          }
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

    FileDB GetFileDB(byte[] iDAccount)
    {
      ushort keyFileDB = BitConverter.ToUInt16(iDAccount, 0);
      return FilesDB[keyFileDB];
    }

    // Validate signature
    static void SpendAccount(TX tX, RecordDBAccounts accountInput)
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
            out RecordDBAccounts account))
          {
            account.Value += outputValueTX;

            if (c != IndexCache)
            {
              Caches[c].Remove(iDAccount);
              AddToCacheIndexed(iDAccount, account);
            }

            break;
          }

          c = (c + Caches.Count - 1) % Caches.Count;

          if (c == IndexCache)
          {
            if (GetFileDB(iDAccount).TryFetchAccount(iDAccount, out account))
              account.Value += outputValueTX;
            else
              account = new RecordDBAccounts
              {
                CountdownToReplay = uint.MaxValue,
                Value = outputValueTX,
                IDAccount = iDAccount
              };

            AddToCacheIndexed(iDAccount, account);

            break;
          }
        }
      }
    }

    void AddToCacheIndexed(byte[] iDAccount, RecordDBAccounts account)
    {
      Caches[IndexCache].Add(iDAccount, account);

      if(Caches[IndexCache].Count > COUNT_MAX_CACHE_INDEXED)
      {
        for (int i = 0; i < COUNT_FILES_DB; i += 1)
          FilesDB[i].TryDefragment();

        IndexCache = (IndexCache + Caches.Count + 1) % Caches.Count;

        foreach(KeyValuePair<byte[], RecordDBAccounts> item in Caches[IndexCache])
          GetFileDB(item.Value.IDAccount).WriteRecordDBAccount(item.Value);

        Caches[IndexCache].Clear();
      }
    }
  }
}
