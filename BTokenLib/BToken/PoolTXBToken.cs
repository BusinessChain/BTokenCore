using System;
using System.Linq;
using System.Collections.Generic;


namespace BTokenLib
{
  public partial class TokenBToken : Token
  {
    public class PoolTXBToken
    {
      class TXBundle
      {
        public byte[] IDAccountSource;
        public long FeeAverageTX;
        public List<TXBToken> TXs = new();

        public TXBundle(TXBToken tX)
        {
          IDAccountSource = tX.IDAccountSource;
          FeeAverageTX = tX.Fee;
          TXs = new List<TXBToken> { tX };
        }
      }

      TokenBToken Token;

      int SequenceNumberTX;

      Dictionary<byte[], (TXBToken tX, int sequenceNumberTX)> TXsByHash = new(new EqualityComparerByteArray());

      Dictionary<byte[], List<TXBToken>> TXsByIDAccountSource = new(new EqualityComparerByteArray());

      Dictionary<byte[], long> OutputValuesByIDAccount = new(new EqualityComparerByteArray());

      /// <summary>
      /// A bundle conains tXs with the same source account with consecutive nonce.
      /// The miner can pull bundles from this list and be sure to have maximized fee.
      /// </summary>
      List<TXBundle> TXBundlesSortedByFee = new();


      public PoolTXBToken(TokenBToken token)
      {
        Token = token;
      }

      public Account GetCopyOfAccount(byte[] accountID)
      {
        Account account;

        account = Token.GetCopyOfAccount(accountID);

        if (TXsByIDAccountSource.TryGetValue(accountID, out List<TXBToken> tXsInPool))
        {
          account.Balance -= tXsInPool.Sum(t => t.GetValueOutputs() + t.Fee);
          account.Nonce += tXsInPool.Count;
        }

        if (OutputValuesByIDAccount.TryGetValue(accountID, out long valueTotal))
          account.Balance += valueTotal;

        return account;
      }

      public void AddTX(TX tX)
      {
        TXBToken tXBToken = tX as TXBToken;

        Account accountSource = GetCopyOfAccount(tXBToken.IDAccountSource);

        if (accountSource.Nonce != tXBToken.Nonce)
          throw new ProtocolException($"Nonce {tXBToken.Nonce} of tX {tXBToken} not equal " +
            $"to unconfirmed nonce {accountSource.Nonce} of account {accountSource}.");

        if (accountSource.Balance < tXBToken.GetValueOutputs() + tXBToken.Fee)
          throw new ProtocolException($"Value {tXBToken.GetValueOutputs() + tXBToken.Fee} of tX {tXBToken} " +
            $"bigger than unconfirmed balance {accountSource.Balance} of account {accountSource}.");

        TXsByHash.Add(tXBToken.Hash, (tXBToken, SequenceNumberTX++));

        if (TXsByIDAccountSource.TryGetValue(tXBToken.IDAccountSource, out List<TXBToken> tXsInPool))
          tXsInPool.Add(tXBToken);
        else
          TXsByIDAccountSource.Add(tXBToken.IDAccountSource, new List<TXBToken>() { tXBToken });

        foreach (TXOutputP2PKH tXOutputBToken in tXBToken.TXOutputs)
          if (tXOutputBToken.Value > 0)
            if (!OutputValuesByIDAccount.TryAdd(tXOutputBToken.IDAccount, tXOutputBToken.Value))
              OutputValuesByIDAccount[tXOutputBToken.IDAccount] += tXOutputBToken.Value;

        InsertTXInTXBundlesSortedByFee(tXBToken);
      }

      public void RemoveTXs(IEnumerable<byte[]> hashesTX)
      {
        foreach (byte[] hashTX in hashesTX)
        {
          if (!TXsByHash.Remove(hashTX, out (TXBToken tX, int sequenceNumberTX) tXsByHashItem))
            continue;

          TXBToken tX = tXsByHashItem.tX;

          List<TXBToken> tXsByAccountSource = TXsByIDAccountSource[tX.IDAccountSource];

          if (!tXsByAccountSource[0].Hash.IsAllBytesEqual(hashTX))
            throw new ProtocolException($"Removal of tX {hashTX.ToHexString()} from pool not in expected order of nonce.");

          tXsByAccountSource.RemoveAt(0);

          if (tXsByAccountSource.Count == 0)
            TXsByIDAccountSource.Remove(tX.IDAccountSource);

          foreach (TXOutputP2PKH tXOutput in tX.TXOutputs)
          {
            OutputValuesByIDAccount[tXOutput.IDAccount] -= tXOutput.Value;

            if (OutputValuesByIDAccount[tXOutput.IDAccount] == 0)
              OutputValuesByIDAccount.Remove(tXOutput.IDAccount);
          }

          TXBundlesSortedByFee.Clear();
          SequenceNumberTX = 0;

          var orderedItems = TXsByHash.OrderBy(i => i.Value.sequenceNumberTX).ToList();

          for (int i = 0; i < orderedItems.Count; i++)
          {
            TXsByHash[orderedItems[i].Key] = (orderedItems[i].Value.tX, SequenceNumberTX++);
            InsertTXInTXBundlesSortedByFee(orderedItems[i].Value.tX);
          }
        }
      }

      void InsertTXInTXBundlesSortedByFee(TXBToken tX)
      {
        TXBundle tXBundle = new(tX);

        int i = TXBundlesSortedByFee.Count - 1;

        while (i > -1)
        {
          TXBundle tXBundleNext = TXBundlesSortedByFee[i];

          if (tXBundle.FeeAverageTX < tXBundleNext.FeeAverageTX)
          {
            TXBundlesSortedByFee.Insert(i + 1, tXBundle);
            return;
          }
          else if (tXBundle.IDAccountSource.IsAllBytesEqual(tXBundleNext.IDAccountSource))
          {
            tXBundleNext.TXs.AddRange(tXBundle.TXs);

            tXBundleNext.FeeAverageTX =
              tXBundleNext.TXs.Sum(t => t.Fee) / tXBundleNext.TXs.Count;

            tXBundle = tXBundleNext;

            TXBundlesSortedByFee.RemoveAt(i);
          }

          i--;
        }

        TXBundlesSortedByFee.Insert(0, tXBundle);
      }

      public List<TX> GetTXs(int countBytesMax)
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
}