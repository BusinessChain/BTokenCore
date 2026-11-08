namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  internal class PoolTXBToken
  {
    class TXBundle
    {
      internal byte[] IDAccountSource;
      internal int SequenceNumberTX;
      internal double FeePerByte;
      internal long Fee;
      internal long CountBytes;
      internal List<TXBToken> TXs = new();

      internal TXBundle(TXBToken tX, int sequenceNumberTX)
      {
        IDAccountSource = tX.IDAccountSource;
        SequenceNumberTX = sequenceNumberTX;
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

    int SequenceNumberTXLowest;

    long CountBytesTXs;

    internal Dictionary<byte[], (TXBToken tX, int sequenceNumberTX)> TXsByHash = new(new EqualityComparerByteArray());

    Dictionary<byte[], (List<TXBundle> tXBundles, Account account)> TXsByIDAccountSource = new(new EqualityComparerByteArray());

    SortedSet<TXBundle> TXBundlesSortedByFee = new(Comparer<TXBundle>.Create((a, b) =>
    {
      int comparisonFeePerByte = b.FeePerByte.CompareTo(a.FeePerByte);

      if (comparisonFeePerByte != 0)
        return comparisonFeePerByte;

      return a.SequenceNumberTX.CompareTo(b.SequenceNumberTX);
    }));


    internal PoolTXBToken(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
    }

    internal void AddTX(TX tX)
    {
      if (!Blockchain.IsAccountsAtTip())
        throw new ProtocolException($"TX {tX.Hash.ToHexString()} cannot be verified without account database.");

      TXBToken tXBToken = tX as TXBToken;

      if (CountBytesTXs + tXBToken.CountBytes > COUNT_BYTES_MAX_POOL &&
        tXBToken.GetFeePerByte() < TXBundlesSortedByFee.Max.FeePerByte + FEE_PER_BYTE_INCREMENT)
        throw new ProtocolException($"TX {tXBToken.Hash.ToHexString()} does not outbid the full pool by the fee increment.");

      if (!TXsByIDAccountSource.TryGetValue(tXBToken.IDAccountSource, out (List<TXBundle> tXBundles, Account account) accountInPool))
        accountInPool = (new List<TXBundle>(), Blockchain.Accounts.LoadAccountExisting(tXBToken.IDAccountSource));

      accountInPool.account.SpendTX(tXBToken);

      TXsByIDAccountSource[tXBToken.IDAccountSource] = accountInPool;

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
        InsertTX((TXBToken)block.TXs[i], isFromBlockRolledBack: true);

      ReverifyAccountsTouched(block);

      EvictTXsOverCap();
    }

    internal void Clear()
    {
      TXsByHash.Clear();
      TXsByIDAccountSource.Clear();
      TXBundlesSortedByFee.Clear();
      CountBytesTXs = 0;
      SequenceNumberTX = 0;
      SequenceNumberTXLowest = 0;
    }

    void EvictTXsOverCap()
    {
      while (CountBytesTXs > COUNT_BYTES_MAX_POOL)
      {
        TXBundle tXBundleLast = TXBundlesSortedByFee.Max;
        TXBToken tXEvicted = tXBundleLast.TXs[^1];

        RemoveTXFromIndexes(tXEvicted);

        TXsByIDAccountSource[tXEvicted.IDAccountSource].account.ReverseSpendTX(tXEvicted);

        RebuildTXBundles(tXBundleLast);
      }
    }

    void RebuildTXBundles(TXBundle tXBundleFirst)
    {
      List<TXBundle> tXBundles = TXsByIDAccountSource[tXBundleFirst.IDAccountSource].tXBundles;

      int index = tXBundles.IndexOf(tXBundleFirst);

      List<TXBundle> tXBundlesRebuilt = tXBundles.GetRange(index, tXBundles.Count - index);

      tXBundles.RemoveRange(index, tXBundlesRebuilt.Count);

      foreach (TXBundle tXBundle in tXBundlesRebuilt)
        TXBundlesSortedByFee.Remove(tXBundle);

      foreach (TXBToken tX in tXBundlesRebuilt.SelectMany(b => b.TXs).Where(t => TXsByHash.ContainsKey(t.Hash)))
        InsertTXInTXBundlesSortedByFee(tX);

      if (tXBundles.Count == 0)
        TXsByIDAccountSource.Remove(tXBundleFirst.IDAccountSource);
    }

    void InsertTX(TXBToken tX, bool isFromBlockRolledBack)
    {
      CountBytesTXs += tX.CountBytes;

      if (!isFromBlockRolledBack)
      {
        TXsByHash.Add(tX.Hash, (tX, SequenceNumberTX++));
        return;
      }

      TXsByHash.Add(tX.Hash, (tX, --SequenceNumberTXLowest));

      if (!TXsByIDAccountSource.TryGetValue(tX.IDAccountSource, out (List<TXBundle> tXBundles, Account account) accountInPool))
        accountInPool = (new List<TXBundle>(), null);

      accountInPool.tXBundles.Insert(0, new TXBundle(tX, SequenceNumberTXLowest));

      TXsByIDAccountSource[tX.IDAccountSource] = accountInPool;
    }

    internal void RemoveTXs(Block block)
    {
      foreach (TX tXConfirmed in block.TXs)
        if (TXsByHash.TryGetValue(tXConfirmed.Hash, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem))
          RemoveTXFromIndexes(tXsByHashItem.tX);

      ReverifyAccountsTouched(block);
    }

    void ReverifyAccountsTouched(Block block)
    {
      HashSet<byte[]> iDsAccountTouched = new(new EqualityComparerByteArray());

      foreach (TXBToken tX in block.TXs)
      {
        iDsAccountTouched.Add(tX.IDAccountSource);

        foreach (TXOutputP2PKH tXOutput in tX.TXOutputs.OfType<TXOutputP2PKH>())
          iDsAccountTouched.Add(tXOutput.IDAccount);
      }

      foreach (byte[] iDAccount in iDsAccountTouched)
      {
        if (!TXsByIDAccountSource.TryGetValue(iDAccount, out (List<TXBundle> tXBundles, Account account) accountInPool))
          continue;

        List<TXBToken> tXsInPool = accountInPool.tXBundles.SelectMany(b => b.TXs).Where(t => TXsByHash.ContainsKey(t.Hash)).ToList();

        Account account = Blockchain.Accounts.LoadAccount(iDAccount, 0);

        for (int i = 0; i < tXsInPool.Count; i += 1)
        {
          try
          {
            account.SpendTX(tXsInPool[i]);
          }
          catch (ProtocolException)
          {
            foreach (TXBToken tXDropped in tXsInPool.Skip(i))
              RemoveTXFromIndexes(tXDropped);

            break;
          }
        }

        TXsByIDAccountSource[iDAccount] = (accountInPool.tXBundles, account);

        RebuildTXBundles(accountInPool.tXBundles[0]);
      }
    }

    void RemoveTXFromIndexes(TXBToken tX)
    {
      TXsByHash.Remove(tX.Hash);

      CountBytesTXs -= tX.CountBytes;
    }

    void InsertTXInTXBundlesSortedByFee(TXBToken tX)
    {
      TXBundle tXBundle = new(tX, TXsByHash[tX.Hash].sequenceNumberTX);

      List<TXBundle> tXBundles = TXsByIDAccountSource[tX.IDAccountSource].tXBundles;

      while (tXBundles.Count > 0 && tXBundles[^1].FeePerByte <= tXBundle.FeePerByte)
      {
        TXBundle tXBundlePrevious = tXBundles[^1];

        TXBundlesSortedByFee.Remove(tXBundlePrevious);
        tXBundles.RemoveAt(tXBundles.Count - 1);

        tXBundlePrevious.TXs.AddRange(tXBundle.TXs);

        tXBundlePrevious.Fee += tXBundle.Fee;
        tXBundlePrevious.CountBytes += tXBundle.CountBytes;
        tXBundlePrevious.FeePerByte = (double)tXBundlePrevious.Fee / tXBundlePrevious.CountBytes;

        tXBundle = tXBundlePrevious;
      }

      TXBundlesSortedByFee.Add(tXBundle);
      tXBundles.Add(tXBundle);
    }

    internal double GetFeePerByteMin()
    {
      if (CountBytesTXs <= COUNT_BYTES_MAX_POOL - SIZE_BLOCK_MAX)
        return 0;

      return TXBundlesSortedByFee.Max.FeePerByte + FEE_PER_BYTE_INCREMENT;
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