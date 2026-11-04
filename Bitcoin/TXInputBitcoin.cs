namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  internal class TXInputBitcoin
  {
    internal byte[] TXIDOutput;
    internal int IndexOutput;
    internal int Sequence;


    internal TXInputBitcoin()
    { }

    internal TXInputBitcoin(byte[] buffer, ref int index)
    {
      TXIDOutput = buffer.ReadBytes(ref index, Crypto.LENGTH_HASH256);

      IndexOutput = buffer.Read<int>(ref index);

      int lengthScript = VarInt.GetInt(buffer, ref index);
      index += lengthScript;

      Sequence = buffer.Read<int>(ref index);
    }
  }
}
