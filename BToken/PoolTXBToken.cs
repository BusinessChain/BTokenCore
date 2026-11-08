namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  internal class PoolTXBToken
  {
    class TXBundle
    {
      internal byte[] IDAccountSource;
      internal double FeePerByte;
      internal long Fee;
      internal long CountBytes;
      internal List<TXBToken> TXs = new();

      internal TXBundle(TXBToken tX)
      {
        IDAccountSource = tX.IDAccountSource;
        FeePerByte = tX.GetFeePerByte();
        Fee = tX.Fee;
        CountBytes = tX.CountBytes;
        TXs = new List<TXBToken> { tX };
      }
    }

    const int COUNT_BYTES_MAX_POOL = 100 * SIZE_BLOCK_MAX;

    const double FEE_PER_BYTE_INCREMENT = 1;

    BlockchainBToken Blockchain;

    int SequenceNumberTX;

    long CountBytesTXs;

    internal Dictionary<byte[], (TXBToken tX, int sequenceNumberTX)> TXsByHash = new(new EqualityComparerByteArray());

    Dictionary<byte[], List<TXBToken>> TXsByIDAccountSource = new(new EqualityComparerByteArray());

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
      if (!Blockchain.IsAccountsAtTip())
        throw new ProtocolException($"TX {tX.Hash.ToHexString()} cannot be verified without account database.");

      TXBToken tXBToken = tX as TXBToken;

      Account accountTrial = Blockchain.Accounts.LoadAccountExisting(tXBToken.IDAccountSource);

      if (TXsByIDAccountSource.TryGetValue(tXBToken.IDAccountSource, out List<TXBToken> tXsInPool))
        foreach (TXBToken tXInPool in tXsInPool)
          accountTrial.SpendTX(tXInPool);

      accountTrial.SpendTX(tXBToken);

      if (CountBytesTXs + tXBToken.CountBytes > COUNT_BYTES_MAX_POOL &&
        tXBToken.GetFeePerByte() < TXBundlesSortedByFee[^1].FeePerByte + FEE_PER_BYTE_INCREMENT)
        throw new ProtocolException($"TX {tXBToken.Hash.ToHexString()} does not outbid the full pool by the fee increment.");

      InsertTX(tXBToken, isFromBlockRolledBack: false);

      InsertTXInTXBundlesSortedByFee(tXBToken);

      EvictTXsOverCap();

      if (!TXsByHash.ContainsKey(tXBToken.Hash))
        throw new ProtocolException($"TX {tXBToken.Hash.ToHexString()} ranks too low for the full pool.");
    }

    internal void ReturnTXs(Block block)
    {
      if (!Blockchain.IsAccountsAtTip())
        return;

      for (int i = block.TXs.Count - 1; i > 0; i--)
      {
        TXBToken tX = (TXBToken)block.TXs[i];

        InsertTX(tX, isFromBlockRolledBack: true);

        SortTXBundlesByFee();
      }

      DropTXsNoLongerCoveredByBalance();

      EvictTXsOverCap();
    }

    internal void Clear()
    {
      TXsByHash.Clear();
      TXsByIDAccountSource.Clear();
      TXBundlesSortedByFee.Clear();
      CountBytesTXs = 0;
      SequenceNumberTX = 0;
    }

    void EvictTXsOverCap()
    {
      bool isTXEvicted = false;

      while (CountBytesTXs > COUNT_BYTES_MAX_POOL)
      {
        TXBundle tXBundleLast = TXBundlesSortedByFee[^1];
        TXBToken tXEvicted = tXBundleLast.TXs[^1];

        tXBundleLast.TXs.RemoveAt(tXBundleLast.TXs.Count - 1);

        if (tXBundleLast.TXs.Count == 0)
          TXBundlesSortedByFee.RemoveAt(TXBundlesSortedByFee.Count - 1);

        RemoveTXFromIndexes(tXEvicted);

        isTXEvicted = true;
      }

      if (isTXEvicted)
        SortTXBundlesByFee();
    }

    void InsertTX(TXBToken tX, bool isFromBlockRolledBack)
    {
      if (isFromBlockRolledBack)
        TXsByHash.Add(tX.Hash, (tX, -1));
      else
        TXsByHash.Add(tX.Hash, (tX, SequenceNumberTX++));

      CountBytesTXs += tX.CountBytes;

      if (!TXsByIDAccountSource.TryGetValue(tX.IDAccountSource, out List<TXBToken> tXsInPool))
      {
        tXsInPool = new List<TXBToken>();
        TXsByIDAccountSource.Add(tX.IDAccountSource, tXsInPool);
      }

      if (isFromBlockRolledBack)
        tXsInPool.Insert(0, tX);
      else
        tXsInPool.Add(tX);
    }

    internal void RemoveTXs(Block block)
    {
      foreach (TX tXConfirmed in block.TXs)
        if (TXsByHash.TryGetValue(tXConfirmed.Hash, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem))
          RemoveTXFromIndexes(tXsByHashItem.tX);

      DropTXsNoLongerCoveredByBalance();
    }

    void DropTXsNoLongerCoveredByBalance()
    {
      foreach (byte[] iDAccountSource in TXsByIDAccountSource.Keys.ToList())
      {
        List<TXBToken> tXsInPool = TXsByIDAccountSource[iDAccountSource];

        Account account = Blockchain.Accounts.LoadAccount(iDAccountSource, 0);

        for (int i = 0; i < tXsInPool.Count; i += 1)
        {
          try
          {
            account.SpendTX(tXsInPool[i]);
          }
          catch (ProtocolException)
          {
            foreach (TXBToken tXDropped in tXsInPool.Skip(i).ToList())
              RemoveTXFromIndexes(tXDropped);

            break;
          }
        }
      }

      SortTXBundlesByFee();
    }

    void RemoveTXFromIndexes(TXBToken tX)
    {
      TXsByHash.Remove(tX.Hash);

      CountBytesTXs -= tX.CountBytes;

      List<TXBToken> tXsInPool = TXsByIDAccountSource[tX.IDAccountSource];

      tXsInPool.Remove(tX);

      if (tXsInPool.Count == 0)
        TXsByIDAccountSource.Remove(tX.IDAccountSource);
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

          tXBundleNext.Fee += tXBundle.Fee;
          tXBundleNext.CountBytes += tXBundle.CountBytes;
          tXBundleNext.FeePerByte = (double)tXBundleNext.Fee / tXBundleNext.CountBytes;

          tXBundle = tXBundleNext;

          TXBundlesSortedByFee.RemoveAt(i);
        }

        i--;
      }

      TXBundlesSortedByFee.Insert(0, tXBundle);
    }

    internal double GetFeePerByteMin()
    {
      if (CountBytesTXs <= COUNT_BYTES_MAX_POOL - SIZE_BLOCK_MAX)
        return 0;

      return TXBundlesSortedByFee[^1].FeePerByte + FEE_PER_BYTE_INCREMENT;
    }

    internal IEnumerable<TXBToken> GetTXsRanked()
    {
      foreach (TXBundle tXBundle in TXBundlesSortedByFee)
        foreach (TXBToken tX in tXBundle.TXs)
          yield return tX;
    }

    internal List<TX> GetTXs(int countBytesMax)
    {
      List<TX> tXs = new();
      int countBytesCurrent = 0;

      foreach (TXBToken tX in GetTXsRanked())
      {
        if (countBytesCurrent + tX.CountBytes > countBytesMax)
          return tXs;

        tXs.Add(tX);

        countBytesCurrent += tX.CountBytes;
      }

      return tXs;
    }
  }
}