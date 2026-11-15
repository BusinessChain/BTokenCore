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

  internal override void ParseBlock(Block block, bool verifySignatures)
  {
    base.ParseBlock(block, verifySignatures);

    if (!((HeaderBToken)block.Header).HashAnchors.IsAllBytesEqual(ComputeHashAnchors(EnumerateAnchorsEligible(block.TXs, block.Header.Height))))
      throw new ProtocolException($"HashAnchors of block {block.Header.Hash.ToHexString()} not equal to the hash of its eligible anchors.");
  }

  internal override TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures)
  {
    return new BlockchainBToken.TXBToken(buffer, ref index, isCoinbase, verifySignatures, IDToken);
  }
}
