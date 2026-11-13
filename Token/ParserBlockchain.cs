using System.Security.Cryptography;


namespace BTokenCore;

internal abstract partial class Blockchain
{
  internal abstract class ParserBlockchain
  {
    internal abstract Header ParseHeader(byte[] buffer, ref int index);

    internal abstract TX ParseTX(byte[] buffer, ref int index, bool isCoinbase, bool verifySignatures);

    internal int ParseBlockHeader(Block block)
    {
      int startIndex = 0;

      Header header = ParseHeader(block.Buffer, ref startIndex);

      if (!block.Header.Hash.IsAllBytesEqual(header.Hash) || !block.Header.HashPrevious.IsAllBytesEqual(header.HashPrevious))
        throw new ProtocolException($"Received unexpected block {header} expected was {block.Header}.");

      header.CopyTo(block.Header);

      return startIndex;
    }

    internal void ParseBlock(Block block, bool verifySignatures)
    {
      int startIndex = ParseBlockHeader(block);

      int countTXs = VarInt.GetInt(block.Buffer, ref startIndex);

      if (countTXs == 0)
        throw new ProtocolException($"Block {block} lacks coinbase transaction.");

      HashSet<byte[]> hashesTX = new(new EqualityComparerByteArray());

      for (int t = 0; t < countTXs; t += 1)
      {
        TX tX = ParseTX(block.Buffer, ref startIndex, isCoinbase: t == 0, verifySignatures);

        if (!hashesTX.Add(tX.Hash))
          throw new ProtocolException($"Block {block.Header.Hash.ToHexString()} contains transaction {tX.Hash.ToHexString()} twice.");

        block.TXs.Add(tX);
      }

      if (!block.Header.MerkleRoot.IsAllBytesEqual(block.ComputeMerkleRoot()))
        throw new ProtocolException("Header merkle root not equal to computed transactions merkle root.");

      DrawAnchorsWinner(block);
    }

    static void DrawAnchorsWinner(Block block)
    {
      Dictionary<byte[], byte[]> scoresAnchorWinner = new(new EqualityComparerByteArray());

      foreach (TX tX in block.TXs)
      {
        if (tX.LockTime != block.Header.Height - 1)
          continue;

        foreach (TXOutput tXOutput in tX.TXOutputs)
          if (tXOutput is TXOutputTokenAnchor tokenAnchor)
          {
            byte[] scoreAnchor = SHA256.HashData([.. block.Header.Hash, .. tokenAnchor.HashBlockReferenced, .. tokenAnchor.HashBlockPreviousReferenced]);

            if (!scoresAnchorWinner.TryGetValue(tokenAnchor.IDToken, out byte[] scoreAnchorWinner)
              || scoreAnchor.IsLessThan(scoreAnchorWinner))
            {
              scoresAnchorWinner[tokenAnchor.IDToken] = scoreAnchor;
              block.Header.AnchorsWinner[tokenAnchor.IDToken] = tokenAnchor;
            }
          }
      }
    }
  }
}
