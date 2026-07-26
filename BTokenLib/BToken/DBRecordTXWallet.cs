using System;
using System.IO;

using LiteDB;


namespace BTokenLib
{
  public partial class TokenBToken : Token
  {
    public class DBRecordTXWallet
    {
      [BsonId]
      public byte[] HashTX = new byte[32];

      [BsonField]
      public int BlockHeightOriginTX;

      [BsonField]
      public int SerialNumberTX;

      [BsonField]
      public byte[] TXRaw;
    }
  }
}