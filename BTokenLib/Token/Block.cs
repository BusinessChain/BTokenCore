using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;

namespace BTokenLib
{
  public abstract class Block
  {
    const int HASH_BYTE_SIZE = 32;

    public Header Header;
    public Block BlockNext;
    public Block BlockChild;

    public SHA256 SHA256 = SHA256.Create();

    public List<TX> TXs = new();

    public byte[] Buffer;

    public long Fee;
    public long FeePerByte;


    public Block()
    { }

    public Block(int sizeBuffer)
    {
      Buffer = new byte[sizeBuffer];
    }


    public void Parse()
    {
      Parse(0);
    }

    public void Parse(int indexBuffer)
    {
      Header = ParseHeader(Buffer, ref indexBuffer);

      ParseTXs(Header.MerkleRoot, ref indexBuffer);

      Header.CountBytesBlock = indexBuffer;
    }

    public abstract Header ParseHeader(
      byte[] buffer,
      ref int index);

    void ParseTXs(
      byte[] hashMerkleRoot,
      ref int bufferIndex)
    {
      TXs.Clear();

      int tXCount = VarInt.GetInt32(
        Buffer,
        ref bufferIndex);

      if (tXCount == 0)
        throw new ProtocolException($"Block {this} lacks coinbase transaction.");
      
      if (tXCount == 1)
        TXs.Add(
          ParseTX(
            isCoinbase: true,
            Buffer,
            ref bufferIndex, 
            SHA256));
      else
      {
        int tXsLengthMod2 = tXCount & 1;
        var merkleList = new byte[tXCount + tXsLengthMod2][];

        TX tX = ParseTX(
          isCoinbase: true,
          Buffer,
          ref bufferIndex, 
          SHA256);

        TXs.Add(tX);

        merkleList[0] = tX.Hash;

        for (int t = 1; t < tXCount; t += 1)
        {
          tX = ParseTX(
          isCoinbase: false,
          Buffer,
          ref bufferIndex,
          SHA256);

          TXs.Add(tX);

          Fee += tX.Fee;

          merkleList[t] = tX.Hash;
        }

        if (tXsLengthMod2 != 0)
          merkleList[tXCount] = merkleList[tXCount - 1];
      }

      if (!hashMerkleRoot.IsEqual(ComputeMerkleRoot()))
        throw new ProtocolException("Payload hash not equal to merkle root.");
    }

    public static TX ParseTX(
      bool isCoinbase,
      byte[] buffer,
      ref int indexBuffer,
      SHA256 sHA256)
    {
      TX tX = new();

      try
      {
        int tXStartIndex = indexBuffer;

        indexBuffer += 4; // Version

        if (buffer[indexBuffer] == 0x00)
          throw new NotImplementedException(
            "Parsing of segwit txs not implemented");

        int countInputs = VarInt.GetInt32(
          buffer,
          ref indexBuffer);

        if (isCoinbase)
          new TXInput(buffer, ref indexBuffer);
        else
          for (int i = 0; i < countInputs; i += 1)
            tX.TXInputs.Add(
              new TXInput(
                buffer,
                ref indexBuffer));

        int countTXOutputs = VarInt.GetInt32(
          buffer,
          ref indexBuffer);

        for (int i = 0; i < countTXOutputs; i += 1)
          tX.TXOutputs.Add(
            new TXOutput(
              buffer,
              ref indexBuffer));

        indexBuffer += 4; //BYTE_LENGTH_LOCK_TIME

        tX.Hash = sHA256.ComputeHash(
         sHA256.ComputeHash(
           buffer,
           tXStartIndex,
           indexBuffer - tXStartIndex));

        tX.TXIDShort = BitConverter.ToInt32(tX.Hash, 0);

        return tX;
      }
      catch (ArgumentOutOfRangeException)
      {
        throw new ProtocolException(
          "ArgumentOutOfRangeException thrown in ParseTX.");
      }
    }

    public byte[] ComputeMerkleRoot()
    {
      if (TXs.Count == 1)
        return TXs[0].Hash;

      int tXsLengthMod2 = TXs.Count & 1;
      var merkleList = new byte[TXs.Count + tXsLengthMod2][];
      int merkleIndex = merkleList.Length;

      for (int i = 0; i < TXs.Count; i += 1)
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

          merkleList[i] =
            SHA256.ComputeHash(
              SHA256.ComputeHash(leafPair));
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

    public override string ToString()
    {
      return Header.ToString();
    }

    public void Clear()
    {
      TXs.Clear();
    }

    public void SetFee(long fee)
    {
      Fee = fee;
      FeePerByte = Fee / Header.CountBytesBlock;
    }
  }
}
