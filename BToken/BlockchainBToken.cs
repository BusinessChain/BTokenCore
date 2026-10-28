using LiteDB;


namespace BTokenCore;

internal class BlockchainBToken : Blockchain
{
  internal BlockchainBToken(
    TokenBToken token,
    BlockchainBitcoin blockchainParent,
    SemaphoreSlim semaphoreBlockchain)
    : base(token, blockchainParent, semaphoreBlockchain)
  { }

  protected override void ParseBlockStored(Block block)
  {
    block.Parse();
  }

  protected override byte[] SerializeBlockStored(Block block)
  {
    DatabaseBlockCollection.DeleteMany(
      Query.LT("_id", ChainRoot.GetHeightPruning()));

    return block.Buffer[..block.LengthDataPayload];
  }
}
