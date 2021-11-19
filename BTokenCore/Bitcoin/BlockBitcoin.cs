using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;

using BTokenLib;


namespace BTokenCore
{
  class BlockBitcoin : Block
  {
    const int HASH_BYTE_SIZE = 32;
    public const int COUNT_HEADER_BYTES = 80;

    public List<UTXOTable.TX> TXs = new();

    SHA256 SHA256 = SHA256.Create();


    public BlockBitcoin()
    { }

    public BlockBitcoin(HeaderBitcoin header) : 
      base(header)
    { }

    public BlockBitcoin(int sizeBuffer)
    {
      Buffer = new byte[sizeBuffer];
    }

    public override void Parse(
      byte[] buffer,
      ref int index)
    {
      Buffer = buffer;
      int startIndex = index;

      Header = ParseHeader(
        Buffer,
        ref index,
        SHA256);

      TXs = ParseTXs(
        Header.MerkleRoot,
        ref index);

      Header.CountBlockBytes = index - startIndex;
    }

    public static HeaderBitcoin ParseHeader(
      byte[] buffer,
      ref int index,
      SHA256 sHA256)
    {
      byte[] hash =
        sHA256.ComputeHash(
          sHA256.ComputeHash(
            buffer,
            index,
            COUNT_HEADER_BYTES));

      uint version = BitConverter.ToUInt32(buffer, index);
      index += 4;

      byte[] previousHeaderHash = new byte[32];
      Array.Copy(buffer, index, previousHeaderHash, 0, 32);
      index += 32;

      byte[] merkleRootHash = new byte[32];
      Array.Copy(buffer, index, merkleRootHash, 0, 32);
      index += 32;

      uint unixTimeSeconds = BitConverter.ToUInt32(
        buffer, index);
      index += 4;

      const long MAX_FUTURE_TIME_SECONDS = 2 * 60 * 60;
      if (unixTimeSeconds >
        (DateTimeOffset.UtcNow.ToUnixTimeSeconds() +
        MAX_FUTURE_TIME_SECONDS))
      {
        throw new ProtocolException(
          string.Format("Timestamp premature {0}",
            new DateTime(unixTimeSeconds).Date));
      }

      uint nBits = BitConverter.ToUInt32(buffer, index);
      index += 4;

      if (hash.IsGreaterThan(nBits))
      {
        throw new ProtocolException(
          $"Header hash {hash.ToHexString()} " +
          $"greater than NBits {nBits}");
      }

      uint nonce = BitConverter.ToUInt32(buffer, index);
      index += 4;

      return new HeaderBitcoin(
        hash,
        version,
        previousHeaderHash,
        merkleRootHash,
        unixTimeSeconds,
        nBits,
        nonce);
    }

    List<UTXOTable.TX> ParseTXs(
      byte[] hashMerkleRoot,
      ref int bufferIndex)
    {
      List<UTXOTable.TX> tXs = new();

      int tXCount = VarInt.GetInt32(
        Buffer,
        ref bufferIndex);

      if (tXCount == 0)
      { }
      else if (tXCount == 1)
      {
        UTXOTable.TX tX = ParseTX(
          isCoinbase: true,
          Buffer,
          ref bufferIndex);

        tXs.Add(tX);

        if (!tX.Hash.IsEqual(hashMerkleRoot))
        {
          throw new ProtocolException(
            "Payload merkle root corrupted");
        }
      }
      else
      {
        int tXsLengthMod2 = tXCount & 1;
        var merkleList = new byte[tXCount + tXsLengthMod2][];

        UTXOTable.TX tX = ParseTX(
          isCoinbase: true,
          Buffer,
          ref bufferIndex);

        tXs.Add(tX);

        merkleList[0] = tX.Hash;

        for (int t = 1; t < tXCount; t += 1)
        {
          tX = ParseTX(
          isCoinbase: false,
          Buffer,
          ref bufferIndex);

          tXs.Add(tX);

          merkleList[t] = tX.Hash;
        }

        if (tXsLengthMod2 != 0)
        {
          merkleList[tXCount] = merkleList[tXCount - 1];
        }

        if (!hashMerkleRoot.IsEqual(GetRoot(merkleList)))
        {
          throw new ProtocolException(
            "Payload hash not equal to merkle root.");
        }
      }

      return tXs;
    }

    public UTXOTable.TX ParseTX(
      bool isCoinbase,
      byte[] buffer,
      ref int indexBuffer)
    {
      UTXOTable.TX tX = new();

      try
      {
        int tXStartIndex = indexBuffer;

        indexBuffer += 4; // BYTE_LENGTH_VERSION

        bool isWitnessFlagPresent = buffer[indexBuffer] == 0x00;
        if (isWitnessFlagPresent)
        {
          throw new NotImplementedException(
            "Parsing of segwit txs not implemented");
          //BufferIndex += 2;
        }

        int countInputs = VarInt.GetInt32(
          buffer, ref indexBuffer);

        if (isCoinbase)
        {
          new UTXOTable.TXInput(buffer, ref indexBuffer);
        }
        else
        {
          for (int i = 0; i < countInputs; i += 1)
          {
            tX.TXInputs.Add(
              new UTXOTable.TXInput(
                buffer,
                ref indexBuffer));
          }
        }

        int countTXOutputs = VarInt.GetInt32(
          buffer,
          ref indexBuffer);

        for (int i = 0; i < countTXOutputs; i += 1)
        {
          tX.TXOutputs.Add(
            new UTXOTable.TXOutput(
              buffer,
              ref indexBuffer));
        }

        //if (isWitnessFlagPresent)
        //{
        //var witnesses = new TXWitness[countInputs];
        //for (int i = 0; i < countInputs; i += 1)
        //{
        //  witnesses[i] = TXWitness.Parse(Buffer, ref BufferIndex);
        //}
        //}

        indexBuffer += 4; //BYTE_LENGTH_LOCK_TIME

        tX.Hash = SHA256.ComputeHash(
         SHA256.ComputeHash(
           buffer,
           tXStartIndex,
           indexBuffer - tXStartIndex));

        tX.TXIDShort = BitConverter.ToInt32(tX.Hash, 0);

        int lengthUTXOBits =
          UTXOTable.COUNT_NON_OUTPUT_BITS + countTXOutputs;

        return tX;
      }
      catch (ArgumentOutOfRangeException)
      {
        throw new ProtocolException(
          "ArgumentOutOfRangeException thrown in ParseTX.");
      }
    }

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
  }
}
