namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  internal class TXInputBitcoin
  {
    const int HASH_BYTE_SIZE = 32;

    internal byte[] TXIDOutput;
    internal int OutputIndex;
    internal int Sequence;


    internal TXInputBitcoin()
    { }

    internal TXInputBitcoin(byte[] buffer, ref int index)
    {
      TXIDOutput = buffer.ReadBytes(ref index, HASH_BYTE_SIZE);

      OutputIndex = buffer.Read<int>(ref index);

      int lengthScript = VarInt.GetInt(buffer, ref index);
      index += lengthScript;

      Sequence = buffer.Read<int>(ref index);
    }
  }
}
