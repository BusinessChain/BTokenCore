using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  partial class DatabaseAccounts
  {
    class CacheDatabase : Dictionary<byte[], RecordDBAccounts>
    {
      public byte[] Hash;
      bool FlagHashOutdated;
      SHA256 SHA256 = SHA256.Create();


      public CacheDatabase() : base(new EqualityComparerByteArray())
      { }

      public void UpdateHash()
      {
        if (FlagHashOutdated)
        {
          int i = 0;
          byte[] bytesCaches = new byte[Values.Count * LENGTH_RECORD_DB];

          foreach(RecordDBAccounts record in Values)
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
      }
    }
  }
}
