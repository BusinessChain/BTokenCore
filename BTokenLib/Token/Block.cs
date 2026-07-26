using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public class Block
    {
      Token Token;

      public Header Header;
      public List<TX> TXs = new();

      // The buffer is constant in size [SizeBlockMax]
      // LengthDataPayload signifies the length of used data in buffer.
      public byte[] Buffer;
      public int LengthDataPayload;

      public SHA256 SHA256 = SHA256.Create();


      public Block(Token token)
        : this(
            token,
            new byte[token.SizeBlockMax])
      { }

      public Block(Token token, byte[] buffer)
      {
        Token = token;
        Buffer = buffer;
      }

      public void Parse()
      {
        TXs.Clear();
        int startIndex = 0;

        Header header = Token.ParseHeader(Buffer, ref startIndex, SHA256);

        if (Header != null && !Header.Hash.IsAllBytesEqual(header.Hash))
          throw new ProtocolException($"Received unexpected block {header} expected was {Header}.");

        if (Header == null)
          Header = header;

        Header.CountTXs = VarInt.GetInt(Buffer, ref startIndex);

        if (Header.CountTXs == 0)
          throw new ProtocolException($"Block {this} lacks coinbase transaction.");

        for (int t = 0; t < Header.CountTXs; t += 1)
          TXs.Add(Token.ParseTX(Buffer, ref startIndex, SHA256, flagIsCoinbase: t == 0));

        if (!Header.MerkleRoot.IsAllBytesEqual(ComputeMerkleRoot()))
          throw new ProtocolException("Header merkle root not equal to computed transactions merkle root.");

        Header.Fee = TXs.Sum(t => t.Fee);

        Header.VerifyCoinbase(TXs[0].GetValueOutputs());
      }

      public byte[] ComputeMerkleRoot()
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

      public void Serialize()
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

      public void WriteToDisk(string pathDirectory)
      {
        string pathFileBlock = Path.Combine(pathDirectory, Header.Height.ToString());
        string pathTemp = pathFileBlock + ".tmp";

        using (FileStream fileStream = new(pathTemp, FileMode.Create, FileAccess.Write))
        {
          byte[] bufferHeader = Header.Serialize();
          fileStream.Write(bufferHeader, 0, bufferHeader.Length);

          byte[] countTXs = VarInt.GetBytes(TXs.Count);
          fileStream.Write(countTXs, 0, countTXs.Length);

          for (int i = 0; i < TXs.Count; i++)
            fileStream.Write(TXs[i].TXRaw, 0, TXs[i].TXRaw.Length);

          fileStream.Flush(true);
        }

        File.Move(pathTemp, pathFileBlock, overwrite: true);
      }

      public override string ToString()
      {
        return Header.ToString();
      }
    }
  }
}
