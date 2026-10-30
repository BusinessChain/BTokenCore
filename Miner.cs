using LiteDB;


namespace BTokenCore;

internal class Miner
{
  BlockchainBitcoin BlockchainBitcoin;
  BlockchainBToken BlockchainBToken;
  Network NetworkBitcoin;

  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();

  LiteDatabase LiteDatabase;
  ILiteCollection<BsonDocument> DatabaseBlocksMinedCollection;


  internal Miner(
    BlockchainBitcoin blockchainBitcoin,
    BlockchainBToken blockchainBToken,
    Network networkBitcoin)
  {
    BlockchainBitcoin = blockchainBitcoin;
    BlockchainBToken = blockchainBToken;
    NetworkBitcoin = networkBitcoin;

    LiteDatabase = new LiteDatabase("Filename=Miner.db;Mode=Exclusive");
    DatabaseBlocksMinedCollection = LiteDatabase.GetCollection<BsonDocument>("blocksMined");
  }

  internal async Task StartMining(long feePerByte)
  {
    // The user has to define the fee rate at which they want to pay for the anchoring.
    // The GUI could also offer a tool that controls the fee rate automatically,
    // e.g. based on past fee rates or market price arbitrage.

    try
    {
      await BlockchainBToken.LockBlockchain();

      FeePerByte = feePerByte;
      IsMining = true;

      MineBlockNext(null);
    }
    finally
    {
      BlockchainBToken.ReleaseLockBlockchain();
    }
  }

  internal void StopMining()
  {
    IsMining = false;
  }

  internal void InsertBlockMined(Block blockBitcoin)
  {
    if (!blockBitcoin.Header.AnchorsWinner.TryGetValue(BlockchainBToken.IDToken, out TXOutputTokenAnchor anchorWinner))
      return;

    Block block = BlocksMinedCache
      .Find(b => b.Header.Hash.IsAllBytesEqual(anchorWinner.HashBlockReferenced));

    if (block == null)
    {
      BsonDocument bsonDocumentBlock = DatabaseBlocksMinedCollection.FindById(anchorWinner.HashBlockReferenced);

      if (bsonDocumentBlock == null)
        return;

      block = new(BlockchainBToken);
      block.LoadBuffer(bsonDocumentBlock["blockBytes"].AsBinary);
    }

    BlocksMinedCache.Remove(block);
    DatabaseBlocksMinedCollection.Delete(anchorWinner.HashBlockReferenced);

    BlockchainBToken.InsertBlockMined(blockBitcoin, block);
  }

  internal void MineBlockNext(Header headerTip)
  {
    if (!IsMining)
      return;

    if (!BlockchainBitcoin.IsTXAnchorOpen())
    {
      BlocksMinedCache.Clear();
      DatabaseBlocksMinedCollection.DeleteAll();
    }

    Block block = BlockchainBToken.MineBlock(out TXOutputTokenAnchor anchorToken);

    if (!BlockchainBitcoin.TryCreateTXAnchor(anchorToken, FeePerByte, out TX tX))
      return;

    block.Serialize();

    BlocksMinedCache.Add(block);

    DatabaseBlocksMinedCollection.Insert(new BsonDocument
    {
      ["_id"] = block.Header.Hash,
      ["blockBytes"] = block.Buffer[..block.LengthDataPayload]
    });

    NetworkBitcoin.Broadcast(tX);
  }
}
