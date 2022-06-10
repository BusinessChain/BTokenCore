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

    public byte[] HashMerkleRoot;
    public List<TX> TXs = new();
    protected List<ushort> IDsBToken = new();

    public byte[] Buffer;

    public long Fee;
    public long FeePerByte;


    public Block()
    { }

    public Block(
      int sizeBuffer)
    {
      Buffer = new byte[sizeBuffer];
    }

    public void Parse()
    {
      int index = 0;

      Header = ParseHeader(Buffer, ref index);

      ParseTXs(Header.MerkleRoot, ref index);

      Header.CountBlockBytes = index;
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

      if (tXCount == 0)
        throw new ProtocolException($"Block {this} lacks coinbase transaction.");
      
      if (tXCount == 1)
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

          Fee += tX.Fee;

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

    public void AppendToBlockchain(Blockchain blockchain)
    {
      Header.AppendToHeader(blockchain.HeaderTip);
      ComputeFee();
    }

    protected virtual void ComputeFee()
    {
      TXs[0].TXOutputs.ForEach(o => Fee += o.Value);

      FeePerByte = Fee / Header.CountBlockBytes;
    }
  }
}
