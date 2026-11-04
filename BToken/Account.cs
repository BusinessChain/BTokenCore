using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  internal class Account
  {
    internal const int LENGTH_ACCOUNT = 36;

    [BsonId]
    internal byte[] ID;

    [BsonField]
    internal int HeightAccountCreated;

    [BsonField]
    internal int Nonce;

    [BsonField]
    internal long Balance;


    internal Account() { }

    internal Account(Account account)
    {
      ID = account.ID;
      HeightAccountCreated = account.HeightAccountCreated;
      Nonce = account.Nonce;
      Balance = account.Balance;
    }

    internal void SpendTX(TXBToken tX)
    {
      if (HeightAccountCreated != tX.HeightAccountCreated || Nonce != tX.Nonce)
        throw new ProtocolException($"Staged account {this} referenced by TX {tX} has unequal nonce or heightAccountCreated.");

      if (Balance < tX.GetValueOutputs() + tX.Fee)
        throw new ProtocolException($"Staged account {this} referenced by TX {tX} does not have enough fund.");

      Nonce += 1;
      Balance -= tX.GetValueOutputs() + tX.Fee;
    }

    internal void ReverseSpendTX(TXBToken tX)
    {
      Nonce = tX.Nonce;
      Balance += tX.GetValueOutputs() + tX.Fee;
    }
  }
}