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
    class CacheDatabaseAccounts : Dictionary<byte[], RecordDBAccounts>
    {
      public byte[] Hash;
      SHA256 SHA256 = SHA256.Create();


      public CacheDatabaseAccounts() : base(new EqualityComparerByteArray())
      { }

      public void UpdateHash()
      {
        int i = 0;
        byte[] bytesCaches = new byte[Values.Count * LENGTH_RECORD_DB];

        foreach (RecordDBAccounts record in Values)
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
          foreach (RecordDBAccounts record in Values)
          {
            file.Write(record.IDAccount);
            file.Write(BitConverter.GetBytes(record.CountdownToReplay));
            file.Write(BitConverter.GetBytes(record.Value));
          }
      }
    }
  }
}
