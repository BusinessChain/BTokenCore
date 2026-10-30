namespace BTokenCore;

internal class BlockchainBitcoin : Blockchain
{
  TokenBitcoin TokenBitcoin;


  internal BlockchainBitcoin(TokenBitcoin token, SemaphoreSlim semaphoreBlockchain)
    : base(token, blockchainParent: null, semaphoreBlockchain)
  {
    TokenBitcoin = token;

    token.LoadDatabase(Database);
  }

  protected override void ParseBlockStored(Block block)
  {
    block.ParseCompact();
  }

  protected override byte[] SerializeBlockStored(Block block)
  {
    return block.SerializeCompact();
  }

  internal async Task<TX> GetTXAnchorOpen(byte[] hash)
  {
    try
    {
      await LockBlockchain();

      TokenBitcoin.TryGetTXAnchorOpen(hash, out TX tX);

      return tX;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}
