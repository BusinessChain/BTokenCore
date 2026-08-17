namespace BTokenCore;

internal interface IToken
{
  internal Block CreateBlock();
  internal void InsertBlock(Block block);
  internal Header CreateHeaderGenesis();
  internal Block MineBlock(int height, out TXOutputTokenAnchor anchorToken);
  internal void ReverseBlock(Block block);
}
