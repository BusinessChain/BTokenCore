namespace BTokenCore;

public partial class TokenBToken : Token
{
  class TXOutputP2PKH : TXOutput
  {
    internal TXOutputP2PKH()
    { }

    internal TXOutputP2PKH(byte[] buffer, int indexScript)
    {
      IDAccount = new byte[TXBToken.LENGTH_IDACCOUNT];

      Array.Copy(buffer, indexScript + PREFIX_P2PKH.Length, IDAccount, 0, TXBToken.LENGTH_IDACCOUNT);
    }
  }
}
