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
    class FileDB : FileStream
    { 
      int TresholdRatioDefragmentation = 10;
      int CountRecords;
      int CountRecordsNullyfied;

      public byte[] Hash;
      bool FlagHashOutdated;
      SHA256 SHA256 = SHA256.Create();



      public FileDB(string path) : base(
        path,
        FileMode.Append,
        FileAccess.Write,
        FileShare.ReadWrite)
      { }

      public void SpendAccountInFileDB(byte[] iDAccount, TX tX)
      {
        Position = 0;

        while (Position < Length)
        {
          int i = 0;

          while (ReadByte() == iDAccount[i++])
            if (i == LENGTH_ID_ACCOUNT)
            {
              byte[] countdownToReplay = new byte[4];
              Read(countdownToReplay);

              byte[] value = new byte[8];
              Read(value);

              RecordDB account = new()
              {
                IDAccount = iDAccount,
                CountdownToReplay = BitConverter.ToUInt32(value),
                Value = BitConverter.ToInt64(value)
              };

              SpendAccount(tX, account);

              if (account.Value > 0)
              {
                Position -= LENGTH_COUNTDOWN_TO_REPLAY + LENGTH_VALUE;
                Write(BitConverter.GetBytes(account.CountdownToReplay));
                Write(BitConverter.GetBytes(account.Value));
              }
              else
              {
                Position -= LENGTH_RECORD_DB;
                Write(new byte[LENGTH_RECORD_DB]);

                CountRecordsNullyfied += 1;
              }

              FlagHashOutdated = true;
              return;
            }

          Position += LENGTH_RECORD_DB - Position % LENGTH_RECORD_DB;
        }

        throw new ProtocolException(
          $"Account {iDAccount.ToHexString()} referenced by TX\n" +
          $"{tX.Hash.ToHexString()} not found in database.");
      }

      public bool TryFetchAccount(
        byte[] iDAccount,
        out RecordDB account)
      {
        Position = 0;

        while (Position < Length)
        {
          int i = 0;
          while (ReadByte() == iDAccount[i++])
            if (i == LENGTH_ID_ACCOUNT)
            {
              byte[] countdownToReplay = new byte[4];
              Read(countdownToReplay);

              byte[] value = new byte[8];
              Read(value);

              account = new()
              {
                IDAccount = iDAccount,
                CountdownToReplay = BitConverter.ToUInt32(value),
                Value = BitConverter.ToInt64(value)
              };

              Position -= LENGTH_RECORD_DB;
              Write(new byte[LENGTH_RECORD_DB]);

              CountRecordsNullyfied += 1;
              FlagHashOutdated = true;

              return true;
            }

          Position += LENGTH_RECORD_DB - Position % LENGTH_RECORD_DB;
        }

        account = null;
        return false;
      }

      public void WriteRecordDBAccount(RecordDB account)
      {
        Seek(Position, SeekOrigin.End);

        Write(account.IDAccount);
        Write(BitConverter.GetBytes(account.CountdownToReplay));
        Write(BitConverter.GetBytes(account.Value));

        CountRecords += 1;

        FlagHashOutdated = true;
      }

      public void Defragment()
      {
        if(CountRecords / CountRecordsNullyfied < TresholdRatioDefragmentation)
        {
          Position = 0;
          byte[] bytesFileDB = new byte[Length];
          Read(bytesFileDB, 0, (int)Length);

          Position = 0;
          Flush();

          for (int i = 0; i < bytesFileDB.Length; i += LENGTH_RECORD_DB)
          {
            int j = 0;
            while (bytesFileDB[i + j] == 0 && j < LENGTH_ID_ACCOUNT)
              j += 1;

            if(j < LENGTH_ID_ACCOUNT)
              Write(bytesFileDB, i, LENGTH_RECORD_DB);
          }

          Flush();

          FlagHashOutdated = true;
        }
      }

      public void UpdateHash()
      {
        if (FlagHashOutdated)
          Hash = SHA256.ComputeHash(this);
      }
    }
  }
}
