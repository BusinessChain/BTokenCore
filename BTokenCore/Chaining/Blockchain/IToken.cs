using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore.Chaining
{
  partial class Blockchain
  {
    public interface IToken
    {
      Header GetHeaderGenesis();
      Dictionary<int, byte[]> GetCheckpoints();

      void LoadImage(string pathImage);
      void CreateImage(string pathImage);
      void Reset();

      void InsertBlock(
        Block block, 
        int indexBlockArchive);

      string GetStatus();

      void ValidateHeader(
        Header header, 
        int height);

      Header ParseHeader(
        byte[] buffer,
        ref int index);

      IBlockParser CreateParser();

      string GetName();
    }
  }
}
