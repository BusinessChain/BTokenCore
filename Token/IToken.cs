using System.Security.Cryptography;

namespace BTokenCore;

internal interface IToken
{
  internal int GetSizeBlockBuffer();
  internal void InsertBlock(Block block);
  internal Header CreateHeaderGenesis();
  internal void MineBlock(int height, Block block, out TXOutputTokenAnchor anchorToken);
  internal void ReverseBlock(Block block);
  
  internal Header ParseHeader(byte[] buffer, ref int startIndex, SHA256 sha256);
  internal TX ParseTX(byte[] buffer, ref int startIndex, SHA256 sha256, bool flagIsCoinbase);
}
