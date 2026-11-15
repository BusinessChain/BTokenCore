using System.Security.Cryptography;


namespace BTokenCore;

internal abstract partial class Blockchain
{
  internal abstract class ParserToken
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

      block.LengthDataPayload = startIndex;

      if (!block.Header.MerkleRoot.IsAllBytesEqual(block.ComputeMerkleRoot()))
        throw new ProtocolException("Header merkle root not equal to computed transactions merkle root.");
    }

    internal static byte[] SerializeAnchorsWinner(Header header)
    {
      List<byte> bytes = [.. VarInt.GetBytes(header.AnchorsWinner.Count)];

      foreach (TXOutputTokenAnchor anchor in header.AnchorsWinner.Values)
      {
        anchor.Serialize();
        bytes.AddRange(anchor.Script);
      }

      return bytes.ToArray();
    }

    internal static void ParseAnchorsWinner(Header header, byte[] buffer, ref int index)
    {
      int countAnchors = VarInt.GetInt(buffer, ref index);

      for (int i = 0; i < countAnchors; i++)
      {
        TXOutputTokenAnchor anchor = new(buffer, ref index);
        header.AnchorsWinner.TryAdd(anchor.IDToken, anchor);
      }
    }

    internal static IEnumerable<TXOutputTokenAnchor> EnumerateAnchorsEligible(List<TX> tXs, int height)
    {
      return tXs.Where(t => t.LockTime == height - 1).SelectMany(t => t.TXOutputs.OfType<TXOutputTokenAnchor>());
    }

    internal static byte[] ComputeHashAnchors(IEnumerable<TXOutputTokenAnchor> anchors)
    {
      return SHA256.HashData(anchors.SelectMany(a => a.IDToken.Concat(a.HashBlockReferenced).Concat(a.HashBlockPreviousReferenced)).ToArray());
    }

    internal static void DrawAnchorsWinner(Block block)
    {
      Dictionary<byte[], byte[]> scoresAnchorWinner = new(new EqualityComparerByteArray());

      Header headerRoot = block.Header;

      while (headerRoot.HeaderParent != null)
        headerRoot = headerRoot.HeaderParent;

      foreach (TXOutputTokenAnchor tokenAnchor in EnumerateAnchorsEligible(block.TXs, block.Header.Height))
      {
        byte[] scoreAnchor = SHA256.HashData([.. headerRoot.Hash, .. tokenAnchor.HashBlockReferenced, .. tokenAnchor.HashBlockPreviousReferenced]);

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
