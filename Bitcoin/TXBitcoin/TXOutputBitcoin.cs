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

    internal TXOutputBitcoin(byte[] buffer, int indexScript)
    {
      indexScript += PREFIX_P2PKH.Length;

      Array.Copy(buffer, indexScript, PublicKeyHash160, 0, PublicKeyHash160.Length);
      indexScript += PublicKeyHash160.Length;

      if (POSTFIX_P2PKH.IsAllBytesEqual(buffer, indexScript))
        Type = TypesToken.P2PKH;
    }
  }
}
