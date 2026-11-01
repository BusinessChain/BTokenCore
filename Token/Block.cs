using System.Security.Cryptography;


namespace BTokenCore;

public class Block
{
  Blockchain Blockchain;

  internal Header Header;
  internal List<TX> TXs = new();

  // The buffer is constant in size [SizeBlockMax]
  // LengthDataPayload signifies the length of used data in buffer.
  internal byte[] Buffer;
  internal int LengthDataPayload;

  SHA256 SHA256 = SHA256.Create();


  internal Block(Blockchain blockchain)
  {
    Blockchain = blockchain;
    Buffer = new byte[blockchain.GetSizeBlockBuffer()];
  }

  internal void LoadBuffer(byte[] bytes)
  {
    bytes.CopyTo(Buffer, 0);
    LengthDataPayload = bytes.Length;
  }

  internal byte[] SerializeCompact()
  {
    List<byte> bytes = [.. Header.Serialize(), .. VarInt.GetBytes(Header.AnchorsWinner.Count)];

    foreach (TXOutputTokenAnchor anchor in Header.AnchorsWinner.Values)
      bytes.AddRange([.. anchor.IDToken, .. anchor.HashBlockReferenced, .. anchor.HashBlockPreviousReferenced]);

    return bytes.ToArray();
  }

  internal byte[] ComputeMerkleRoot()
  {
    const int HASH_BYTE_SIZE = 32;

    if (TXs.Count == 1)
      return TXs[0].Hash;

    int tXsLengthMod2 = TXs.Count & 1;
    var merkleList = new byte[TXs.Count + tXsLengthMod2][];
    int merkleIndex = merkleList.Length;

    for (int i = 0; i < TXs.Count; i++)
      merkleList[i] = TXs[i].Hash;

    if (tXsLengthMod2 != 0)
      merkleList[TXs.Count] = merkleList[TXs.Count - 1];

    byte[] leafPair = new byte[2 * HASH_BYTE_SIZE];

    while (true)
    {
      merkleIndex >>= 1;

      for (int i = 0; i < merkleIndex; i++)
      {
        int i2 = i << 1;
        merkleList[i2].CopyTo(leafPair, 0);
        merkleList[i2 + 1].CopyTo(leafPair, HASH_BYTE_SIZE);

        merkleList[i] = SHA256.ComputeHash(SHA256.ComputeHash(leafPair));
      }

      if (merkleIndex == 1)
        return merkleList[0];

      if ((merkleIndex & 1) != 0)
      {
        merkleList[merkleIndex] = merkleList[merkleIndex - 1];
        merkleIndex += 1;
      }
    }
  }

  internal void Serialize()
  {
    int startIndex = 0;

    byte[] bufferHeader = Header.Serialize();

    bufferHeader.CopyTo(Buffer, startIndex);
    startIndex += bufferHeader.Length;

    byte[] countTXs = VarInt.GetBytes(TXs.Count);
    countTXs.CopyTo(Buffer, startIndex);
    startIndex += countTXs.Length;

    for (int i = 0; i < TXs.Count; i++)
    {
      TXs[i].TXRaw.CopyTo(Buffer, startIndex);
      startIndex += TXs[i].TXRaw.Length;
    }

    LengthDataPayload = startIndex;
  }
}
