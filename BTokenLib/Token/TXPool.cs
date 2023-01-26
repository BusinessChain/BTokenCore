using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public class TXPool
  {
    List<TX> TXPoolList = new();
    readonly object LOCK_TXsPool = new();

    public TXPool()
    { }


    public void RemoveTXs(IEnumerable<byte[]> hashes)
    {
      lock (LOCK_TXsPool)
      {
        TXPoolList.RemoveAll(
          tX => hashes.Any(hash => hash.IsEqual(tX.Hash)));
      }
    }

    public List<TX> GetTXs(out int countTXs)
    {
      lock (LOCK_TXsPool)
      {
        countTXs = TXPoolList.Count;
        return TXPoolList;
      }
    }

    public void AddTX(TX tX)
    {
      lock (LOCK_TXsPool)
        TXPoolList.Add(tX);
    }

    public int GetCountTXs()
    {
      lock (LOCK_TXsPool)
        return TXPoolList.Count;
    }
  }
}
