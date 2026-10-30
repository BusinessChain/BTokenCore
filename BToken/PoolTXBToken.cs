using System;
using System.Linq;
using System.Collections.Generic;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  internal class PoolTXBToken
  {
    class TXBundle
    {
      internal byte[] IDAccountSource;
      internal double FeePerByte;
      internal List<TXBToken> TXs = new();

      internal TXBundle(TXBToken tX)
      {
        IDAccountSource = tX.IDAccountSource;
        FeePerByte = (double)tX.Fee / tX.CountBytes;
        TXs = new List<TXBToken> { tX };
      }
    }

    BlockchainBToken Blockchain;

    int SequenceNumberTX;

    internal Dictionary<byte[], (TXBToken tX, int sequenceNumberTX)> TXsByHash = new(new EqualityComparerByteArray());

    Dictionary<byte[], List<TXBToken>> TXsByIDAccountSource = new(new EqualityComparerByteArray());

    Dictionary<byte[], long> OutputValuesByIDAccount = new(new EqualityComparerByteArray());

    /// <summary>
    /// A bundle conains tXs with the same source account with consecutive nonce.
    /// The miner can pull bundles from this list and be sure to have maximized fee.
    /// </summary>
    List<TXBundle> TXBundlesSortedByFee = new();


    internal PoolTXBToken(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
    }

    internal void AddTX(TX tX)
    {
      TXBToken tXBToken = tX as TXBToken;

      Account accountTrial = Blockchain.Accounts.GetCopyOfAccount(tXBToken.IDAccountSource);

      if (TXsByIDAccountSource.TryGetValue(tXBToken.IDAccountSource, out List<TXBToken> tXsInPool))
      {
        accountTrial.Balance -= tXsInPool.Sum(t => t.GetValueOutputs() + t.Fee);
        accountTrial.Nonce += tXsInPool.Count;
      }

      if (OutputValuesByIDAccount.TryGetValue(tXBToken.IDAccountSource, out long valueTotal))
        accountTrial.Balance += valueTotal;

      accountTrial.SpendTX(tXBToken);

      InsertTX(tXBToken, flagAhead: false);

      InsertTXInTXBundlesSortedByFee(tXBToken);
    }

    internal void ReturnTXs(Block block)
    {
      for (int i = block.TXs.Count - 1; i > 0; i--)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        InsertTX(tX, flagAhead: true);

        SortTXBundlesByFee();
      }

      DropTXsNoLongerCoveredByBalance();
    }

    void InsertTX(TXBToken tX, bool flagAhead)
    {
      if (flagAhead)
        TXsByHash.Add(tX.Hash, (tX, -1));
      else
        TXsByHash.Add(tX.Hash, (tX, SequenceNumberTX++));

      if (!TXsByIDAccountSource.TryGetValue(tX.IDAccountSource, out List<TXBToken> tXsInPool))
      {
        tXsInPool = new List<TXBToken>();
        TXsByIDAccountSource.Add(tX.IDAccountSource, tXsInPool);
      }

      if (flagAhead)
        tXsInPool.Insert(0, tX);
      else
        tXsInPool.Add(tX);

      foreach (TXOutput tXOutputBToken in tX.TXOutputs)
        if (tXOutputBToken.Value > 0)
          if (!OutputValuesByIDAccount.TryAdd(tXOutputBToken.IDAccount, tXOutputBToken.Value))
            OutputValuesByIDAccount[tXOutputBToken.IDAccount] += tXOutputBToken.Value;
    }

    internal void RemoveTXs(Block block)
    {
      foreach (TX tXConfirmed in block.TXs)
      {
        if (!TXsByHash.Remove(tXConfirmed.Hash,out (TXBToken tX, int sequenceNumberTX) tXsByHashItem))
          continue;

        TXBToken tX = tXsByHashItem.tX;

        List<TXBToken> tXsByAccountSource = TXsByIDAccountSource[tX.IDAccountSource];

        if (!tXsByAccountSource[0].Hash.IsAllBytesEqual(tXConfirmed.Hash))
          throw new ProtocolException($"Removal of tX {tXConfirmed.Hash.ToHexString()} from pool not in expected order of nonce.");

        tXsByAccountSource.RemoveAt(0);

        if (tXsByAccountSource.Count == 0)
          TXsByIDAccountSource.Remove(tX.IDAccountSource);

        RemoveOutputValues(tX);

        SortTXBundlesByFee();
      }
    }

    void DropTXsNoLongerCoveredByBalance()
    {
      bool flagTXDropped;

      do
      {
        flagTXDropped = false;

        foreach (byte[] iDAccountSource in TXsByIDAccountSource.Keys.ToList())
        {
          List<TXBToken> tXsInPool = TXsByIDAccountSource[iDAccountSource];

          Account account = Blockchain.Accounts.LoadAccount(iDAccountSource, 0);

          if (OutputValuesByIDAccount.TryGetValue(iDAccountSource, out long valueTotal))
            account.Balance += valueTotal;

          for (int i = 0; i < tXsInPool.Count; i += 1)
          {
            try
            {
              account.SpendTX(tXsInPool[i]);
            }
            catch (ProtocolException)
            {
              foreach (TXBToken tXDropped in tXsInPool.Skip(i))
              {
                TXsByHash.Remove(tXDropped.Hash);
                RemoveOutputValues(tXDropped);
              }

              tXsInPool.RemoveRange(i, tXsInPool.Count - i);

              if (tXsInPool.Count == 0)
                TXsByIDAccountSource.Remove(iDAccountSource);

              flagTXDropped = true;
              break;
            }
          }
        }
      } while (flagTXDropped);

      SortTXBundlesByFee();
    }

    void RemoveOutputValues(TXBToken tX)
    {
      foreach (TXOutput tXOutput in tX.TXOutputs)
        if (tXOutput.Value > 0)
        {
          OutputValuesByIDAccount[tXOutput.IDAccount] -= tXOutput.Value;

          if (OutputValuesByIDAccount[tXOutput.IDAccount] == 0)
            OutputValuesByIDAccount.Remove(tXOutput.IDAccount);
        }
    }

    void SortTXBundlesByFee()
    {
      TXBundlesSortedByFee.Clear();
      SequenceNumberTX = 0;

      var orderedItems = TXsByHash.OrderBy(i => i.Value.sequenceNumberTX).ToList();

      for (int i = 0; i < orderedItems.Count; i++)
      {
        TXsByHash[orderedItems[i].Key] = (orderedItems[i].Value.tX, SequenceNumberTX++);
        InsertTXInTXBundlesSortedByFee(orderedItems[i].Value.tX);
      }
    }

    void InsertTXInTXBundlesSortedByFee(TXBToken tX)
    {
      TXBundle tXBundle = new(tX);

      int i = TXBundlesSortedByFee.Count - 1;

      while (i > -1)
      {
        TXBundle tXBundleNext = TXBundlesSortedByFee[i];

        if (tXBundle.FeePerByte < tXBundleNext.FeePerByte)
        {
          TXBundlesSortedByFee.Insert(i + 1, tXBundle);
          return;
        }
        else if (tXBundle.IDAccountSource.IsAllBytesEqual(tXBundleNext.IDAccountSource))
        {
          tXBundleNext.TXs.AddRange(tXBundle.TXs);

          tXBundleNext.FeePerByte =
            (double)tXBundleNext.TXs.Sum(t => t.Fee) / tXBundleNext.TXs.Sum(t => t.CountBytes);

          tXBundle = tXBundleNext;

          TXBundlesSortedByFee.RemoveAt(i);
        }

        i--;
      }

      TXBundlesSortedByFee.Insert(0, tXBundle);
    }

    internal List<TX> GetTXs(int countBytesMax)
    {
      List<TX> tXs = new();
      int countBytesCurrent = 0;

      for (int i = 0; i < TXBundlesSortedByFee.Count; i++)
        for (int j = 0; j < TXBundlesSortedByFee[i].TXs.Count; j++)
        {
          if (countBytesCurrent + TXBundlesSortedByFee[i].TXs[j].CountBytes > countBytesMax)
            return tXs;

          tXs.Add(TXBundlesSortedByFee[i].TXs[j]);

          countBytesCurrent += TXBundlesSortedByFee[i].TXs[j].CountBytes;
        }

      return tXs;
    }
  }
}