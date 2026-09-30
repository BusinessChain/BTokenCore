using LiteDB;


namespace BTokenCore;

internal class Miner
{
  Blockchain BlockchainBitcoin;
  Blockchain BlockchainBToken;

  bool IsMining;
  long FeePerByte;
  List<Block> BlocksMinedCache = new();

  LiteDatabase LiteDatabase;
  ILiteCollection<BsonDocument> DatabaseBlocksMinedCollection;


  internal Miner(Blockchain blockchainBitcoin, Blockchain blockchainBToken)
  {
    BlockchainBitcoin = blockchainBitcoin;
    BlockchainBToken = blockchainBToken;

    LiteDatabase = new LiteDatabase("Filename=Miner.db;Mode=Exclusive");
    DatabaseBlocksMinedCollection = LiteDatabase.GetCollection<BsonDocument>("blocksMined");
  }

  internal void OnBlockBitcoinInserted(Block blockBitcoin)
  {
    blockBitcoin.Header.AnchorsWinner.TryGetValue(BlockchainBToken.Token.IDToken, out TXOutputTokenAnchor anchorWinner);

    try
    {
      if (anchorWinner != null)
        InsertBlockMined(anchorWinner);

      if (IsMining)
        MineBlockNext();
    }
    catch
    {
      return;
    }
  }

  void InsertBlockMined(TXOutputTokenAnchor anchorWinner)
  {
    if (!TryGetBlockMined(out Block block, anchorWinner.HashBlockReferenced))
      return;

    BlockchainBToken.InsertBlockMined(block);

    BlocksMinedCache.Remove(block);
    DatabaseBlocksMinedCollection.Delete(anchorWinner.HashBlockReferenced);
  }

  void MineBlockNext()
  {
    // The user has to define the fee rate at which they want to pay for the anchoring.
    // The GUI could also offer a tool that controls the fee rate automatically,
    // e.g. based on past fee rates or market price arbitrage.

    Block block = BlockchainBToken.Token.MineBlock(
      BlockchainBToken.BlockchainRoot.HeaderTipBlockchain,
      out TXOutputTokenAnchor anchorToken);

    block.Serialize();

    BlocksMinedCache.Add(block);

    DatabaseBlocksMinedCollection.Insert(new BsonDocument
    {
      ["_id"] = block.Header.Hash,
      ["blockBytes"] = block.Buffer
    });

    MineTokenAnchor(anchorToken);
  }

  bool TryGetBlockMined(out Block block, byte[] hash)
  {
    block = BlocksMinedCache
      .Find(b => b.Header.Hash.IsAllBytesEqual(hash));

    if (block == null)
    {
      BsonDocument bsonDocumentBlock = DatabaseBlocksMinedCollection.FindById(hash);

      if (bsonDocumentBlock == null)
        return false;

      block = new(BlockchainBToken.Token, bsonDocumentBlock["blockBytes"].AsBinary);
      block.Parse();
    }

    return true;
  }

  void MineTokenAnchor(TXOutputTokenAnchor tokenAnchor)
  {
    if (BlockchainBitcoin.Token.TryCreateTXAnchor(tokenAnchor, FeePerByte, out TX tX))
      BlockchainBitcoin.Broadcast(tX);
    else
    {
      IsMining = false;
    }
  }
}
