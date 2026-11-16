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

    internal virtual void ParseBlock(Block block, bool verifySignatures)
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

    internal static byte[] SerializeAnchors(IEnumerable<TXOutputTokenAnchor> anchors)
    {
      List<TXOutputTokenAnchor> anchorsSerialized = anchors.ToList();
      List<byte> bytes = [.. VarInt.GetBytes(anchorsSerialized.Count)];

      foreach (TXOutputTokenAnchor anchor in anchorsSerialized)
      {
        anchor.Serialize();
        bytes.AddRange(anchor.Script);
      }

      return bytes.ToArray();
    }

    internal static List<TXOutputTokenAnchor> ParseAnchors(byte[] buffer, ref int index)
    {
      int countAnchors = VarInt.GetInt(buffer, ref index);
      List<TXOutputTokenAnchor> anchors = new();

      for (int i = 0; i < countAnchors; i++)
        anchors.Add(new(buffer, ref index));

      return anchors;
    }

    internal static IEnumerable<TXOutputTokenAnchor> EnumerateAnchorsEligible(List<TX> tXs, int height)
    {
      return tXs.Where(t => t.LockTime == height - 1).SelectMany(t => t.TXOutputs.OfType<TXOutputTokenAnchor>());
    }

    internal static byte[] ComputeHashAnchors(IEnumerable<TXOutputTokenAnchor> anchors)
    {
      return SHA256.HashData(anchors.SelectMany(a => a.IDToken.Concat(a.HashBlockReferenced).Concat(a.HashBlockPreviousReferenced)).ToArray());
    }

    internal static void DrawAnchorsWinner(Header header, IEnumerable<TXOutputTokenAnchor> anchors, int countLevelsBelowRoot)
    {
      Dictionary<byte[], byte[]> scoresAnchorWinner = new(new EqualityComparerByteArray());

      Header headerRoot = header;

      for (int level = 0; level < countLevelsBelowRoot; level++)
      {
        headerRoot = headerRoot.HeaderParent;

        if (headerRoot == null)
          return;
      }

      foreach (TXOutputTokenAnchor tokenAnchor in anchors)
      {
        byte[] scoreAnchor = SHA256.HashData([.. headerRoot.Hash, .. tokenAnchor.HashBlockReferenced, .. tokenAnchor.HashBlockPreviousReferenced]);

        if (!scoresAnchorWinner.TryGetValue(tokenAnchor.IDToken, out byte[] scoreAnchorWinner)
          || scoreAnchor.IsLessThan(scoreAnchorWinner))
        {
          scoresAnchorWinner[tokenAnchor.IDToken] = scoreAnchor;
          header.AnchorsWinner[tokenAnchor.IDToken] = tokenAnchor;
        }
      }
    }
  }
}
