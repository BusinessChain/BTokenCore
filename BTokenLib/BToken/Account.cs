using System;
using System.IO;

using LiteDB;


namespace BTokenLib
{
  public partial class TokenBToken : Token
  {
    public class Account
    {
      public const int LENGTH_ACCOUNT = 40;
      public const int LENGTH_ID = 20;

      [BsonId]
      public byte[] ID = new byte[LENGTH_ID];

      [BsonField]
      public int BlockHeightAccountCreated;

      [BsonField]
      public int BlockHeightLastUpdated;

      [BsonField]
      public int Nonce;

      [BsonField]
      public long Balance;

      byte[] ByteArraySerialized = new byte[LENGTH_ACCOUNT];


      public Account() { }

      public Account(Account account) 
      {
        ID = account.ID;
        BlockHeightAccountCreated = account.BlockHeightAccountCreated;
        Nonce = account.Nonce;
        Balance = account.Balance;
      }

      public void SpendTX(TXBToken tX)
      {
        if (BlockHeightAccountCreated != tX.BlockheightAccountCreated || Nonce != tX.Nonce)
          throw new ProtocolException($"Staged account {this} referenced by TX {tX} has unequal nonce or blockheightAccountInit.");

        if (Balance < tX.GetValueOutputs() + tX.Fee)
          throw new ProtocolException($"Staged account {this} referenced by TX {tX} does not have enough fund.");

        Nonce += 1;
        Balance -= tX.GetValueOutputs() + tX.Fee;
      }

      public void ReverseSpendTX(TXBToken tX)
      {
        Nonce -= 1;
        Balance += tX.GetValueOutputs() + tX.Fee;
      }

      public override string ToString()
      {
        return ID.BinaryToBase58Check();
      }
    }
  }
}