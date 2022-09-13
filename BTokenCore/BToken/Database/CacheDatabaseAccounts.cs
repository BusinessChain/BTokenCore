using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  partial class DatabaseAccounts
  {
    class CacheDatabaseAccounts : Dictionary<byte[], RecordDB>
    {
      public byte[] Hash;
      SHA256 SHA256 = SHA256.Create();


      public CacheDatabaseAccounts() : base(new EqualityComparerByteArray())
      { }

      public void UpdateHash()
      {
        int i = 0;
        byte[] bytesCaches = new byte[Values.Count * LENGTH_RECORD_DB];

        foreach (RecordDB record in Values)
        {
          record.IDAccount.CopyTo(bytesCaches, i);
          i += 32;
          BitConverter.GetBytes(record.CountdownToReplay)
            .CopyTo(bytesCaches, i);
          i += 4;
          BitConverter.GetBytes(record.Value)
            .CopyTo(bytesCaches, i);
          i += 8;
        }

        Hash = SHA256.ComputeHash(bytesCaches);
      }

      public void CreateImage(string path)
      {
        using (FileStream file = new(path, FileMode.Create))
          foreach (RecordDB record in Values)
          {
            file.Write(record.IDAccount);
            file.Write(BitConverter.GetBytes(record.CountdownToReplay));
            file.Write(BitConverter.GetBytes(record.Value));
          }
      }
    
      public byte[] GetBytes()
      {
        byte[] dataDB = new byte[LENGTH_RECORD_DB * Count];
        int index = 0;

        foreach(RecordDB recordDB in Values)
        {
          BitConverter.GetBytes(recordDB.CountdownToReplay).CopyTo(dataDB, index);
          index += 4;

          BitConverter.GetBytes(recordDB.Value).CopyTo(dataDB, index);
          index += 8;

          recordDB.IDAccount.CopyTo(dataDB, index);
          index += 32;
        }

        return dataDB;
      }
    }
  }
}
