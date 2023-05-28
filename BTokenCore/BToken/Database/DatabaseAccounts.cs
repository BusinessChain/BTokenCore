using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  partial class DatabaseAccounts
  {
    public const int COUNT_CACHES = 256;
    byte[] HashesCaches = new byte[COUNT_CACHES * 32];
    const int COUNT_MAX_CACHE = 40000; // Read from configuration file
    List<CacheDatabaseAccounts> Caches = new();
    int IndexCache;

    const string PathRootDB = "FilesDB";
    public const int COUNT_FILES_DB = 256; // file index will be idAccount[0]
    List<FileDB> FilesDB = new();
    byte[] HashesFilesDB = new byte[COUNT_FILES_DB * 32];

    const int LENGTH_RECORD_DB = 37;
    const int LENGTH_ID_ACCOUNT = 25;
    const int LENGTH_COUNTDOWN_TO_REPLAY = 4;
    const int LENGTH_VALUE = 8;

    SHA256 SHA256 = SHA256.Create();
    byte[] Hash;

    public DatabaseAccounts()
    {
      for (int i = 0; i < COUNT_CACHES; i += 1)
        Caches.Add(new CacheDatabaseAccounts());

      Directory.CreateDirectory(PathRootDB);

      for (int i = 0; i < COUNT_FILES_DB; i += 1)
        FilesDB.Add(new FileDB(
          Path.Combine(PathRootDB, i.ToString())));
    }

    public void LoadImage(string path)
    {
      byte[] bytesRecord = new byte[LENGTH_RECORD_DB];

      for (int i = 0; i < COUNT_CACHES; i += 1)
        using (FileStream fileCache = new(
          Path.Combine(path, "cache", i.ToString()),
          FileMode.Open))
        {
          while(fileCache.Read(bytesRecord) == LENGTH_RECORD_DB)
          {
            RecordDB record = new()
            {
              IDAccount = bytesRecord.Take(LENGTH_ID_ACCOUNT).ToArray(),
              CountdownToReplay = BitConverter.ToUInt32(bytesRecord, LENGTH_ID_ACCOUNT),
              Value = BitConverter.ToInt64(bytesRecord, LENGTH_ID_ACCOUNT + LENGTH_COUNTDOWN_TO_REPLAY)
            };

            Caches[i].Add(record.IDAccount, record);
          }
        }
    }

    public void CreateImage(string path)
    {
      string pathDirectoryCache = Path.Combine(path, "cache");

      Directory.CreateDirectory(pathDirectoryCache);

      for (int i = 0; i < COUNT_CACHES; i += 1)
        Caches[i].CreateImage(
          Path.Combine(pathDirectoryCache, i.ToString()));
    }

    public void ClearCache()
    {
      Caches.ForEach(c => c.Clear());
    }

    public void Delete()
    {
      FilesDB.ForEach(f => { 
        f.Dispose();
        File.Delete(f.Name);
      });

      ClearCache();
    }

    public void InsertDB(
      byte[] bufferDB,
      int lengthDataInBuffer)
    {
      int index = 0;

      if (bufferDB[index++] == 0x00)
      {
        FileDB fileDB = new FileDB(
          Path.Combine(
            PathRootDB,
            bufferDB[index].ToString()));

        FilesDB.Add(fileDB);

        fileDB.Write(bufferDB, index, lengthDataInBuffer - 1);
      }
      else if (bufferDB[index++] == 0x01)
      {
        int indexCache = bufferDB[index++];

        while (index < lengthDataInBuffer)
        {
          RecordDB recordDB = new();

          recordDB.CountdownToReplay = BitConverter.ToUInt32(bufferDB, index);
          index += 4;

          recordDB.Value = BitConverter.ToUInt32(bufferDB, index);
          index += 8;

          Array.Copy(bufferDB, index, recordDB.IDAccount, 0, 32);
          index += 32;

          Caches[indexCache].Add(recordDB.IDAccount, recordDB);
        }
      }
      else
        throw new ProtocolException(
          $"Invalid prefix {bufferDB[index]} in serialized DB data.");
    }

    public void InsertBlock(BlockBToken block)
    {
      List<TX> tXs = block.TXs;

      long feeBlock = 0;

      for (int t = 1; t < tXs.Count; t++)
      {
        byte[] iDAccount = tXs[t].TXInputs[0].TXIDOutput;

        int c = IndexCache;
        while (true)
        {
          if (Caches[c].TryGetValue(iDAccount, out RecordDB account))
          {
            SpendAccount(tXs[t], account);

            if (account.Value == 0)
              Caches[c].Remove(iDAccount);

            break;
          }

          c = (c + COUNT_CACHES - 1) % COUNT_CACHES;

          if (c == IndexCache)
          {
            GetFileDB(iDAccount).SpendAccountInFileDB(iDAccount, tXs[t]);
            break;
          }
        }

        InsertOutputs(tXs[t].TXOutputs);
        
        feeBlock += tXs[t].Fee;
      }
      
      block.SetFee(feeBlock);

      InsertOutputs(tXs[0].TXOutputs);

      UpdateHashDatabase();

      // Statt den aktuellen DB Hash, könnte auch der diesem Block vorangehende DB Hash
      // aufgeführt werden. Dies hätte beim Mining der vorteil, dass das Inserten des 
      // gemineden Block nicht durchgespielt werden müsste.

      //if(!Hash.IsEqual(((HeaderBToken)block.Header).HashDatabase))
      //  throw new ProtocolException(
      //    $"Hash database not equal as given in header {block},\n" +
      //    $"height {block.Header.Height}.");

    }

    void UpdateHashDatabase()
    {
      for (int i = 0; i < COUNT_CACHES; i += 1)
      {
        Caches[i].UpdateHash();

        Caches[i].Hash.CopyTo(
          HashesCaches, 
          i * Caches[i].Hash.Length);
      }

      byte[] hashCaches = SHA256.ComputeHash(HashesCaches);

      for (int i = 0; i < COUNT_FILES_DB; i += 1)
      {
        FilesDB[i].UpdateHash();

        FilesDB[i].Hash.CopyTo(
          HashesFilesDB, 
          i * FilesDB[i].Hash.Length);
      }

      byte[] hashFilesDB = SHA256.ComputeHash(HashesFilesDB);
            
      Hash = SHA256.ComputeHash(
        hashCaches.Concat(hashFilesDB).ToArray());
    }

    FileDB GetFileDB(byte[] iDAccount)
    {
      return FilesDB[iDAccount[0]];
    }

    public bool TryGetDB(
      byte[] hash,
      out byte[] dataDB)
    {
      for (int i = 0; i < HashesFilesDB.Length; i += 1)
        if (hash.IsEqual(HashesFilesDB, i * 32))
        {
          dataDB = new byte[FilesDB[i].Length];
          FilesDB[i].Write(dataDB, 0, dataDB.Length);
          return true;
        }

      for (int i = 0; i < HashesCaches.Length; i += 1)
        if (hash.IsEqual(HashesCaches, i * 32))
        {
          dataDB = Caches[i].GetBytes();
          return true;
        }

      dataDB = null;
      return false;
    }

    // Validate signature
    static void SpendAccount(TX tX, RecordDB accountInput)
    {
      long valueSpend = tX.Fee;
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
        byte[] iDAccount = tXOutputs[i].Buffer
          .Skip(tXOutputs[i].StartIndexScript)
          .Take(LENGTH_ID_ACCOUNT).ToArray();

        long outputValueTX = tXOutputs[i].Value;

        int c = IndexCache;

        while (true)
        {
          if (Caches[c].TryGetValue(
            iDAccount,
            out RecordDB account))
          {
            account.Value += outputValueTX;

            if (c != IndexCache)
            {
              Caches[c].Remove(iDAccount);
              AddToCacheIndexed(iDAccount, account);
            }

            break;
          }

          c = (c + COUNT_CACHES - 1) % COUNT_CACHES;

          if (c == IndexCache)
          {
            if (GetFileDB(iDAccount).TryFetchAccount(iDAccount, out account))
              account.Value += outputValueTX;
            else
              account = new RecordDB
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

    void AddToCacheIndexed(byte[] iDAccount, RecordDB account)
    {
      Caches[IndexCache].Add(iDAccount, account);

      if(Caches[IndexCache].Count > COUNT_MAX_CACHE)
      {
        for (int i = 0; i < COUNT_FILES_DB; i += 1)
          FilesDB[i].Defragment();

        IndexCache = (IndexCache + COUNT_CACHES + 1) % COUNT_CACHES;

        foreach(KeyValuePair<byte[], RecordDB> item in Caches[IndexCache])
          GetFileDB(item.Value.IDAccount).WriteRecordDBAccount(item.Value);

        Caches[IndexCache].Clear();
      }
    }

    public long GetCountBytes()
    {
      long countBytes = 0;

      Caches.ForEach(c => countBytes += c.Count * LENGTH_RECORD_DB);
      FilesDB.ForEach(f => countBytes += f.Length);

      return countBytes;
    }
  }
}
