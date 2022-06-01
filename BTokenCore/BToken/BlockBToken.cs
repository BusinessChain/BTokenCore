using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

using BTokenLib;


namespace BTokenCore
{
  class BlockBToken : Block
  {
    public const int COUNT_HEADER_BYTES = 80;



    public BlockBToken()
    { }

    public BlockBToken(int sizeBuffer)
    {
      Buffer = new byte[sizeBuffer];
    }


    public override HeaderBToken ParseHeader(
      byte[] buffer,
      ref int index)
    {
      byte[] hash =
        SHA256.ComputeHash(
          SHA256.ComputeHash(
            buffer,
            index,
            COUNT_HEADER_BYTES));

      byte[] hashHeaderPrevious = new byte[32];
      Array.Copy(buffer, index, hashHeaderPrevious, 0, 32);
      index += 32;

      byte[] hashAnchorPrevious = new byte[32];
      Array.Copy(buffer, index, hashAnchorPrevious, 0, 32);
      index += 32;

      byte[] merkleRootHash = new byte[32];
      Array.Copy(buffer, index, merkleRootHash, 0, 32);
      index += 32;

      uint unixTimeSeconds = BitConverter.ToUInt32(
        buffer, index);
      index += 4;

      uint nonce = BitConverter.ToUInt32(buffer, index);
      index += 4;

      return new HeaderBToken(
        hash,
        hashHeaderPrevious,
        merkleRootHash,
        unixTimeSeconds,
        nonce);
    }

    public override TX ParseTX(
      bool isCoinbase,
      byte[] buffer,
      ref int indexBuffer)
    {
      TXBToken tX = new();

      try
      {
        int tXStartIndex = indexBuffer;

        int countInputs = VarInt.GetInt32(
          buffer,
          ref indexBuffer);

        //if (isCoinbase)
        //  new UTXOTable.TXInput(buffer, ref indexBuffer);
        //else
        //  for (int i = 0; i < countInputs; i += 1)
        //    tX.TXInputs.Add(
        //      new UTXOTable.TXInput(
        //        buffer,
        //        ref indexBuffer));

        int countTXOutputs = VarInt.GetInt32(
          buffer,
          ref indexBuffer);

        //for (int i = 0; i < countTXOutputs; i += 1)
        //  tX.TXOutputs.Add(
        //    new UTXOTable.TXOutput(
        //      buffer,
        //      ref indexBuffer));

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

  }
}
