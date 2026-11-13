namespace BTokenCore;

internal abstract partial class Blockchain
{
  internal abstract class ParserBlockchain
  {
    internal abstract Header ParseHeader(byte[] buffer, ref int index);

    internal abstract TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures);
  }
}
