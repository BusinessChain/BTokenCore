using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

using BTokenLib;


namespace BTokenCore
{
  class BlockBitcoin : Block
  {
    public const int COUNT_HEADER_BYTES = 80;

    const long VALUE_REWARD_INITIAL_SATOSHI = 5000000000;
    const int NUMBER_OF_BLOCKS_HALFING_CYCLE = 210000;


    public BlockBitcoin()
    { }

    public BlockBitcoin(int sizeBuffer)
      : base(sizeBuffer)
    { }

    public override HeaderBitcoin ParseHeader(
      byte[] buffer,
      ref int index)
    {
      byte[] hash =
        SHA256.ComputeHash(
          SHA256.ComputeHash(
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

      bool isBlockTimePremature = unixTimeSeconds >
        (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 * 60 * 60);
      
      if (isBlockTimePremature)
        throw new ProtocolException(
          $"Timestamp premature {new DateTime(unixTimeSeconds).Date}.");

      uint nBits = BitConverter.ToUInt32(buffer, index);
      index += 4;

      if (hash.IsGreaterThan(nBits))
        throw new ProtocolException(
          $"Header hash {hash.ToHexString()} greater than NBits {nBits}.");

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

    public override TX ParseTX(
      bool isCoinbase,
      byte[] buffer,
      ref int indexBuffer)
    {
      TXBitcoin tX = new();

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
