namespace BTokenCore;

internal class ParserBToken : Blockchain.ParserBlockchain
{
  internal override HeaderBToken ParseHeader(byte[] buffer, ref int index)
  {
    return new HeaderBToken(buffer, ref index);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures)
  {
    return new BlockchainBToken.TXBToken(buffer, ref index, isCoinbase, verifySignatures);
  }
}
