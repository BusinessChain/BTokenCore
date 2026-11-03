namespace BTokenCore;


public class TXOutputP2PKH : TXOutput
{
  internal const byte LENGTH_SCRIPT_P2PKH = 25;
  internal const int LENGTH_HASH160 = 20;
  internal static byte[] PREFIX_P2PKH = [0x76, 0xA9, 0x14];
  internal static byte[] POSTFIX_P2PKH = [0x88, 0xAC];

  internal byte[] IDAccount;

  internal long Value;


  internal TXOutputP2PKH(long value, byte[] iDAccount)
  {
    Value = value;
    IDAccount = iDAccount;
    Script = [.. PREFIX_P2PKH, .. iDAccount, .. POSTFIX_P2PKH];
  }

  internal TXOutputP2PKH(long value, byte[] buffer, int indexScript)
  {
    Value = value;

    IDAccount = new byte[LENGTH_HASH160];

    Array.Copy(buffer, indexScript + PREFIX_P2PKH.Length, IDAccount, 0, LENGTH_HASH160);
  }
}
