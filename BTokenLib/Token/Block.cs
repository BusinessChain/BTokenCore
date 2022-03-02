using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace BTokenLib
{
  public abstract class Block
  {
    const int HASH_BYTE_SIZE = 32;

    public Header Header;

    protected SHA256 SHA256 = SHA256.Create();

    public List<TX> TXs = new();

    public byte[] Buffer;


    public Block()
    { }

    public Block(Header header)
    {
      Header = header;
    }


    public void Parse()
    {
      int index = 0;
      Parse(Buffer, ref index);
    }

    public void Parse(
      byte[] buffer,
      ref int index)
    {
      Buffer = buffer;
      int startIndex = index;

      Header = ParseHeader(
        Buffer,
        ref index);

      ParseTXs(
        Header.MerkleRoot,
        ref index);

      Header.CountBlockBytes = index - startIndex;
    }

    public abstract Header ParseHeader(
      byte[] buffer,
      ref int index);


    void ParseTXs(
      byte[] hashMerkleRoot,
      ref int bufferIndex)
    {
      int tXCount = VarInt.GetInt32(
        Buffer,
        ref bufferIndex);

      if (tXCount == 0) ;
      else if (tXCount == 1)
      {
        TX tX = ParseTX(
          isCoinbase: true,
          Buffer,
          ref bufferIndex);

        TXs.Add(tX);

        if (!tX.Hash.IsEqual(hashMerkleRoot))
          throw new ProtocolException(
            "Payload merkle root corrupted");
      }
      else
      {
        int tXsLengthMod2 = tXCount & 1;
        var merkleList = new byte[tXCount + tXsLengthMod2][];

        TX tX = ParseTX(
          isCoinbase: true,
          Buffer,
          ref bufferIndex);

        TXs.Add(tX);

        merkleList[0] = tX.Hash;

        for (int t = 1; t < tXCount; t += 1)
        {
          tX = ParseTX(
          isCoinbase: false,
          Buffer,
          ref bufferIndex);

          TXs.Add(tX);

          merkleList[t] = tX.Hash;
        }

        if (tXsLengthMod2 != 0)
          merkleList[tXCount] = merkleList[tXCount - 1];

        if (!hashMerkleRoot.IsEqual(GetRoot(merkleList)))
          throw new ProtocolException(
            "Payload hash not equal to merkle root.");
      }
    }

    public abstract TX ParseTX(
      bool isCoinbase,
      byte[] buffer,
      ref int indexBuffer);


    byte[] GetRoot(byte[][] merkleList)
    {
      int merkleIndex = merkleList.Length;

      while (true)
      {
        merkleIndex >>= 1;

        if (merkleIndex == 1)
        {
          ComputeNextMerkleList(merkleList, merkleIndex);
          return merkleList[0];
        }

        ComputeNextMerkleList(merkleList, merkleIndex);

        if ((merkleIndex & 1) != 0)
        {
          merkleList[merkleIndex] = merkleList[merkleIndex - 1];
          merkleIndex += 1;
        }
      }
    }

    void ComputeNextMerkleList(
      byte[][] merkleList,
      int merkleIndex)
    {
      byte[] leafPair = new byte[2 * HASH_BYTE_SIZE];

      for (int i = 0; i < merkleIndex; i++)
      {
        int i2 = i << 1;
        merkleList[i2].CopyTo(leafPair, 0);
        merkleList[i2 + 1].CopyTo(leafPair, HASH_BYTE_SIZE);

        merkleList[i] =
          SHA256.ComputeHash(
            SHA256.ComputeHash(leafPair));
      }
    }

    public override string ToString()
    {
      return Header.ToString();
    }

    public void Clear()
    {
      TXs.Clear();
    }
  }
}
