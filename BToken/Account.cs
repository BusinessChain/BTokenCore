using System;
using System.IO;

using LiteDB;


namespace BTokenCore;

public partial class TokenBToken : Token
{
  internal class Account
  {
    internal const int LENGTH_ACCOUNT = 40;
    internal const int LENGTH_ID = 20;

    [BsonId]
    internal byte[] ID = new byte[LENGTH_ID];

    [BsonField]
    internal int BlockHeightAccountCreated;

    [BsonField]
    internal int BlockHeightLastUpdated;

    [BsonField]
    internal int Nonce;

    [BsonField]
    internal long Balance;


    internal Account() { }

    internal Account(Account account)
    {
      ID = account.ID;
      BlockHeightAccountCreated = account.BlockHeightAccountCreated;
      Nonce = account.Nonce;
      Balance = account.Balance;
    }

    internal void SpendTX(TXBToken tX)
    {
      if (BlockHeightAccountCreated != tX.BlockheightAccountCreated || Nonce != tX.Nonce)
        throw new ProtocolException($"Staged account {this} referenced by TX {tX} has unequal nonce or blockheightAccountInit.");

      if (Balance < tX.GetValueOutputs() + tX.Fee)
        throw new ProtocolException($"Staged account {this} referenced by TX {tX} does not have enough fund.");

      Nonce += 1;
      Balance -= tX.GetValueOutputs() + tX.Fee;
    }

    internal void ReverseSpendTX(TXBToken tX)
    {
      Nonce -= 1;
      Balance += tX.GetValueOutputs() + tX.Fee;
    }
  }
}