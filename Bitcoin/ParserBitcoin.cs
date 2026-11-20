namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  class ParserBitcoin : ParserToken
  {
    internal override Header ParseHeader(byte[] buffer, ref int index)
    {
      return new HeaderBitcoin(buffer, ref index);
    }

    internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures)
    {
      return new TXBitcoin(buffer, ref index, isCoinbase);
    }
  }
}
