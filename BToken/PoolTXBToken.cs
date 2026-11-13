namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class PoolTXBToken
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

    const int COUNT_TXS_MAX_ACCOUNT = 25;

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

    Dictionary<byte[], int> CountTXsPayingByID = new(new EqualityComparerByteArray());

    HashSet<byte[]> IDsCreatedPending = new(new EqualityComparerByteArray());

    int[] CountsCreatedPending = new int[COUNT_SEGMENTS];

    int[] CountsAccountNext;

    long CountAccountsMaxNext;


    internal PoolTXBToken(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
    }

    internal void AddTX(TX tX)
    {
      if (!Blockchain.Synchronizer.IsAccountsAtTip())
        throw new ProtocolException($"TX {tX.Hash.ToHexString()} cannot be verified without account database.");

      TXBToken tXBToken = tX as TXBToken;

      if (!tXBToken.IsLockTimeValid(Blockchain.GetHeight() + 1))
        throw new ProtocolException($"Anchor TX {tXBToken.Hash.ToHexString()} with LockTime {tXBToken.LockTime} is invalid for the next block.");

      if (CountBytesTXs + tXBToken.CountBytes > COUNT_BYTES_MAX_POOL &&
        tXBToken.GetFeePerByte() < TXBundlesSortedByFee.Max.FeePerByte + FEE_PER_BYTE_INCREMENT)
        throw new ProtocolException($"TX {tXBToken.Hash.ToHexString()} does not outbid the full pool by the fee increment.");

      if (!TXsByIDAccountSource.TryGetValue(tXBToken.IDAccountSource, out (List<TXBundle> tXBundles, Account account) accountInPool))
        accountInPool = (
          new List<TXBundle>(),
          Blockchain.Accounts.LoadAccountNext(tXBToken.IDAccountSource)
            ?? throw new ProtocolException($"Account {tXBToken.IDAccountSource.ToHexString()} of TX {tXBToken.Hash.ToHexString()} does not exist after the next sweep."));

      if (accountInPool.tXBundles.Sum(b => b.TXs.Count) >= COUNT_TXS_MAX_ACCOUNT)
        throw new ProtocolException($"TX {tXBToken.Hash.ToHexString()} exceeds the {COUNT_TXS_MAX_ACCOUNT} pool TXs allowed per account.");

      List<byte[]> iDsCreated = GetIDsPaid(tXBToken)
        .Where(id => !IsAccountExistingNext(id))
        .ToList();

      foreach (IGrouping<int, byte[]> iDsCreatedSegment in iDsCreated.GroupBy(DatabaseAccounts.GetIndexSegment))
        if (!IsSegmentOpen(iDsCreatedSegment.Key, iDsCreatedSegment.Count()))
          throw new ProtocolException(
            $"TX {tXBToken.Hash.ToHexString()} pays to {string.Join(", ", iDsCreatedSegment.Select(id => id.BinaryToBase58Check()))}, " +
            $"whose segment {iDsCreatedSegment.Key} accepts no new accounts at the moment. Ask the receiver for a different address.");

      accountInPool.account.SpendTX(tXBToken);

      TXsByIDAccountSource[tXBToken.IDAccountSource] = accountInPool;

      InsertTX(tXBToken, isFromBlockRolledBack: false);

      foreach (byte[] iDCreated in iDsCreated)
        SetCreationPending(iDCreated, isCreationPending: true);

      InsertTXInTXBundlesSortedByFee(tXBToken);

      EvictTXsOverCap();

      if (!TXsByHash.ContainsKey(tXBToken.Hash))
        throw new ProtocolException($"TX {tXBToken.Hash.ToHexString()} ranks too low for the full pool.");
    }

    internal void ReturnTXs(Block block)
    {
      if (!Blockchain.Synchronizer.IsAccountsAtTip())
        return;

      for (int i = block.TXs.Count - 1; i > 0; i--)
        InsertTX((TXBToken)block.TXs[i], isFromBlockRolledBack: true);

      RemoveTXsLockTimeInvalid(block.Header.Height);

      CountsAccountNext = null;

      ReverifyAccounts(TXsByIDAccountSource.Keys.ToList());

      RetestCreations(CountTXsPayingByID.Keys.ToList());

      EvictTXsOverCap();
    }

    internal void Clear()
    {
      TXsByHash.Clear();
      TXsByIDAccountSource.Clear();
      TXBundlesSortedByFee.Clear();
      CountTXsPayingByID.Clear();
      IDsCreatedPending.Clear();
      CountsCreatedPending = new int[COUNT_SEGMENTS];
      CountsAccountNext = null;
      CountBytesTXs = 0;
      SequenceNumberTX = 0;
      SequenceNumberTXLowest = 0;
    }

    static IEnumerable<byte[]> GetIDsPaid(TXBToken tX)
    {
      return tX.TXOutputs
        .OfType<TXOutputP2PKH>()
        .Where(o => o.Value > 0)
        .Select(o => o.IDAccount)
        .Distinct(new EqualityComparerByteArray());
    }

    internal bool CanReceive(byte[] iDAccount)
    {
      return IsAccountExistingNext(iDAccount) || IsSegmentOpen(DatabaseAccounts.GetIndexSegment(iDAccount), 1);
    }

    bool IsAccountExistingNext(byte[] iDAccount)
    {
      return CountTXsPayingByID.ContainsKey(iDAccount) || Blockchain.Accounts.LoadAccountNext(iDAccount) != null;
    }

    bool IsSegmentOpen(int indexSegment, int countAccountsCreated)
    {
      LoadCountsNextIfMissing();

      return CountsAccountNext[indexSegment] + CountsCreatedPending[indexSegment] + countAccountsCreated <= CountAccountsMaxNext;
    }

    void LoadCountsNextIfMissing()
    {
      if (CountsAccountNext == null)
        (CountsAccountNext, CountAccountsMaxNext) = Blockchain.Accounts.LoadCountsAccountsPerSegmentNext();
    }

    void SetCreationPending(byte[] iDAccount, bool isCreationPending)
    {
      if (isCreationPending ? IDsCreatedPending.Add(iDAccount) : IDsCreatedPending.Remove(iDAccount))
        CountsCreatedPending[DatabaseAccounts.GetIndexSegment(iDAccount)] += isCreationPending ? 1 : -1;
    }

    void RetestCreations(IEnumerable<byte[]> iDsAccount)
    {
      foreach (byte[] iDAccount in iDsAccount.Where(CountTXsPayingByID.ContainsKey))
        SetCreationPending(iDAccount, Blockchain.Accounts.LoadAccountNext(iDAccount) == null);

      if (IDsCreatedPending.Count == 0)
        return;

      HashSet<byte[]> iDsAccountSourceDropped = new(new EqualityComparerByteArray());

      for (int indexSegment = 0; indexSegment < COUNT_SEGMENTS; indexSegment += 1)
      {
        if (IsSegmentOpen(indexSegment, 0))
          continue;

        List<TXBToken> tXsCreatingNewestFirst = TXsByHash.Values
          .OrderByDescending(t => t.sequenceNumberTX)
          .Select(t => t.tX)
          .Where(t => GetIDsPaid(t).Any(id => IDsCreatedPending.Contains(id) && DatabaseAccounts.GetIndexSegment(id) == indexSegment))
          .ToList();

        foreach (TXBToken tX in tXsCreatingNewestFirst)
        {
          if (IsSegmentOpen(indexSegment, 0))
            break;

          RemoveTXFromIndexes(tX);
          iDsAccountSourceDropped.Add(tX.IDAccountSource);
        }
      }

      ReverifyAccounts(iDsAccountSourceDropped);
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

      foreach (byte[] iDPaid in GetIDsPaid(tX))
        CountTXsPayingByID[iDPaid] = CountTXsPayingByID.GetValueOrDefault(iDPaid) + 1;

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

      CountsAccountNext = null;

      int indexSegmentSweepNext = Blockchain.Accounts.GetIndexSegmentSweepNext();
      Func<byte[], bool> isInSweepNext = iD => DatabaseAccounts.GetIndexSegment(iD) == indexSegmentSweepNext;

      HashSet<byte[]> iDsAccountReverified = new(
        TXsByIDAccountSource.Keys.Where(isInSweepNext),
        new EqualityComparerByteArray());

      foreach (TXBToken tX in block.TXs)
      {
        iDsAccountReverified.Add(tX.IDAccountSource);

        foreach (TXOutputP2PKH tXOutput in tX.TXOutputs.OfType<TXOutputP2PKH>())
          iDsAccountReverified.Add(tXOutput.IDAccount);
      }

      iDsAccountReverified.UnionWith(RemoveTXsLockTimeInvalid(block.Header.Height + 1));

      ReverifyAccounts(iDsAccountReverified);

      RetestCreations(IDsCreatedPending
        .Concat(iDsAccountReverified)
        .Concat(CountTXsPayingByID.Keys.Where(isInSweepNext))
        .ToList());
    }

    List<byte[]> RemoveTXsLockTimeInvalid(int heightBlockNext)
    {
      List<TXBToken> tXsInvalid = TXsByHash.Values
        .Select(t => t.tX)
        .Where(t => !t.IsLockTimeValid(heightBlockNext))
        .ToList();

      foreach (TXBToken tX in tXsInvalid)
        RemoveTXFromIndexes(tX);

      return tXsInvalid.Select(t => t.IDAccountSource).ToList();
    }

    void ReverifyAccounts(IEnumerable<byte[]> iDsAccount)
    {
      foreach (byte[] iDAccount in iDsAccount)
      {
        if (!TXsByIDAccountSource.TryGetValue(iDAccount, out (List<TXBundle> tXBundles, Account account) accountInPool))
          continue;

        List<TXBToken> tXsInPool = accountInPool.tXBundles.SelectMany(b => b.TXs).Where(t => TXsByHash.ContainsKey(t.Hash)).ToList();

        Account account = Blockchain.Accounts.LoadAccountNext(iDAccount) ?? new Account { ID = iDAccount };

        int countTXsValid = 0;

        while (countTXsValid < tXsInPool.Count)
        {
          try
          {
            account.SpendTX(tXsInPool[countTXsValid]);
          }
          catch (ProtocolException)
          {
            break;
          }

          countTXsValid += 1;
        }

        foreach (TXBToken tXDropped in tXsInPool.Skip(countTXsValid))
          RemoveTXFromIndexes(tXDropped);

        TXsByIDAccountSource[iDAccount] = (accountInPool.tXBundles, account);

        RebuildTXBundles(accountInPool.tXBundles[0]);
      }
    }

    void RemoveTXFromIndexes(TXBToken tX)
    {
      TXsByHash.Remove(tX.Hash);

      CountBytesTXs -= tX.CountBytes;

      foreach (byte[] iDPaid in GetIDsPaid(tX))
        if (--CountTXsPayingByID[iDPaid] == 0)
        {
          CountTXsPayingByID.Remove(iDPaid);
          SetCreationPending(iDPaid, isCreationPending: false);
        }
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