namespace BTokenCore;


public class TXOutputP2PKH : TXOutput
{
  internal TXOutputP2PKH()
  { }

  internal TXOutputP2PKH(byte[] buffer, int indexScript)
  {
    IDAccount = new byte[Token.LENGTH_HASH160];

    Array.Copy(buffer, indexScript + Token.PREFIX_P2PKH.Length, IDAccount, 0, Token.LENGTH_HASH160);
  }
}
