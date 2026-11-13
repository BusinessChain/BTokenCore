namespace BTokenCore;

internal class ParserBitcoin : Blockchain.ParserToken
{
  internal override Header ParseHeader(byte[] buffer, ref int index)
  {
    return new HeaderBitcoin(buffer, ref index);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures)
  {
    return new BlockchainBitcoin.TXBitcoin(buffer, ref index, isCoinbase);
  }
}
