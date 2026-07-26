using System;


namespace BTokenLib
{
  public partial class TokenBitcoin : Token
  {
    public class TXInputBitcoin
    {
      const int HASH_BYTE_SIZE = 32;

      public byte[] TXIDOutput;
      public int OutputIndex;
      public int Sequence;


      public TXInputBitcoin()
      { }

      public TXInputBitcoin(byte[] buffer, ref int index)
      {
        TXIDOutput = new byte[HASH_BYTE_SIZE];

        Array.Copy(buffer, index, TXIDOutput, 0, HASH_BYTE_SIZE);
        index += HASH_BYTE_SIZE;

        OutputIndex = BitConverter.ToInt32(buffer, index);
        index += 4;

        int lengthScript = VarInt.GetInt(buffer, ref index);
        index += lengthScript;

        Sequence = BitConverter.ToInt32(buffer, index);
        index += 4;
      }
    }
  }
}
