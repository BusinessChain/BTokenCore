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
    class FileDB : FileStream
    {
      int TresholdRatioDefragmentation = 5;
      int CountRecords;
      int CountRecordsNullyfied;


      public FileDB(string path) : base(
        path,
        FileMode.Append,
        FileAccess.ReadWrite,
        FileShare.ReadWrite)
      { }

      public void SpendAccountInDB(byte[] iDAccount, TX tX)
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

              RecordDBAccounts account = new()
              {
                IDAccount = iDAccount,
                CountdownToReplay = BitConverter.ToUInt32(value),
                Value = BitConverter.ToUInt64(value)
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
        out RecordDBAccounts account)
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
                Value = BitConverter.ToUInt64(value)
              };

              Position -= LENGTH_RECORD_DB;
              Write(new byte[LENGTH_RECORD_DB]);

              CountRecordsNullyfied += 1;

              return true;
            }

          Position += LENGTH_RECORD_DB - Position % LENGTH_RECORD_DB;
        }

        account = null;
        return false;
      }

      public void WriteRecordDBAccount(RecordDBAccounts account)
      {
        Seek(0, SeekOrigin.End);

        Write(account.IDAccount);
        Write(BitConverter.GetBytes(account.CountdownToReplay));
        Write(BitConverter.GetBytes(account.Value));

        CountRecords += 1;
      }

      public void TryDefragment()
      {
        if(CountRecords / CountRecordsNullyfied >= TresholdRatioDefragmentation)
        {

        }
      }
    }
  }
}
