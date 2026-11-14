using System.Text;


namespace BTokenCore;

internal class ParserBToken : Blockchain.ParserToken
{
  internal byte[] IDToken = new byte[TXOutputTokenAnchor.LENGTH_ID_TOKEN];


  internal ParserBToken(string iDToken)
  {
    Encoding.ASCII.GetBytes(iDToken).CopyTo(IDToken, 0);
  }

  internal override HeaderBToken ParseHeader(byte[] buffer, ref int index)
  {
    return new HeaderBToken(buffer, ref index);
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures)
  {
    return new BlockchainBToken.TXBToken(buffer, ref index, isCoinbase, verifySignatures, IDToken);
  }
}
