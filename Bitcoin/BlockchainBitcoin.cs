namespace BTokenCore;

internal class BlockchainBitcoin : Blockchain
{
  internal BlockchainBitcoin(TokenBitcoin token, SemaphoreSlim semaphoreBlockchain)
    : base(token, blockchainParent: null, semaphoreBlockchain)
  { }

  protected override void ParseBlockStored(Block block)
  {
    block.ParseCompact();
  }

  protected override byte[] SerializeBlockStored(Block block)
  {
    return block.SerializeCompact();
  }
}
