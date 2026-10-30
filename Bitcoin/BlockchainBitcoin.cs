using LiteDB;


namespace BTokenCore;

internal partial class BlockchainBitcoin : Blockchain
{
  internal BlockchainBitcoin(SemaphoreSlim semaphoreBlockchain)
    : base("TokenBitcoin", blockchainParent: null, semaphoreBlockchain)
  {
    SizeBlockMax = SIZE_BLOCK_MAX;

    OutputsOwned = Database.GetCollection<TXOutputOwned>(NAME_COLLECTION_OUTPUTS_OWNED);
    AnchorsOwned = Database.GetCollection<BsonDocument>(NAME_COLLECTION_ANCHORS_OWNED);

    if (AnchorsOwned.FindOne(Query.EQ("heightConfirmed", 0)) is BsonDocument documentAnchor)
      LoadTXAnchorOpen(documentAnchor);
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

      if (TXAnchorOpen != null && TXAnchorOpen.Hash.IsAllBytesEqual(hash))
        return TXAnchorOpen;

      return null;
    }
    finally
    {
      ReleaseLockBlockchain();
    }
  }
}
