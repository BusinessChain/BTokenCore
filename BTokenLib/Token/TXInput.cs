using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;



namespace BTokenLib
{
  public struct TXInput
  {
    public int StartIndexScript;
    public int LengthScript;

    public byte[] TXIDOutput;
    public int TXIDOutputShort;
    public int OutputIndex;

    const int HASH_BYTE_SIZE = 32;



    //public TXInput(
    //  byte[] tXIDOutput,
    //  int outputIndex)
    //{
    //  TXIDOutput = tXIDOutput;
    //  PrimaryKeyTXIDOutput = BitConverter.ToInt32(tXIDOutput, 0);
    //  OutputIndex = outputIndex;
    //}


    public TXInput(byte[] buffer, ref int index)
    {
      TXIDOutput = new byte[HASH_BYTE_SIZE];

      Array.Copy(
        buffer,
        index,
        TXIDOutput,
        0,
        HASH_BYTE_SIZE);

      TXIDOutputShort = BitConverter.ToInt32(
        buffer,
        index);

      index += HASH_BYTE_SIZE;

      OutputIndex = BitConverter.ToInt32(
        buffer,
        index);

      index += 4;

      LengthScript = VarInt.GetInt32(
        buffer,
        ref index);

      StartIndexScript = index;

      index += LengthScript;

      index += 4; // sequence
    }
  }
}
