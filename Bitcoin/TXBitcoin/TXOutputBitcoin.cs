using System;


namespace BTokenCore;

public partial class TokenBitcoin : Token
{
  class TXOutputBitcoin : TXOutput
  {
    internal byte[] PublicKeyHash160 = new byte[20];

    internal byte[] Data;

    internal byte[] Script;


    internal TXOutputBitcoin() { }

    internal TXOutputBitcoin(byte[] buffer, ref int startIndex)
    {
      startIndex += PREFIX_P2PKH.Length;

      Array.Copy(buffer, startIndex, PublicKeyHash160, 0, PublicKeyHash160.Length);
      startIndex += PublicKeyHash160.Length;

      if (POSTFIX_P2PKH.IsAllBytesEqual(buffer, startIndex))
      {
        startIndex += POSTFIX_P2PKH.Length;
        Type = TypesToken.P2PKH;
      }
    }
  }
}
