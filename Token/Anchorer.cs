using LiteDB;


namespace BTokenCore;

internal abstract partial class Blockchain
{
  internal abstract class Anchorer
  {
    protected Blockchain Blockchain;
    protected Network Network;
    internal Wallet Wallet;

    protected ILiteCollection<BsonDocument> AnchorsStored;

    const string NAME_FIELD_TX_RAW = "tXRaw";
    const string NAME_FIELD_FEE = "fee";
    const string NAME_FIELD_HEIGHT_CONFIRMED = "heightConfirmed";
    const string NAME_FIELD_TIME_SENT = "timeSent";


    protected Anchorer(Blockchain blockchain, Network network, LiteDatabase liteDatabase)
    {
      Blockchain = blockchain;
      Network = network;
      Wallet = new Wallet(blockchain.NameToken);

      AnchorsStored = liteDatabase.GetCollection<BsonDocument>("anchors");

      blockchain.OnBlockInsertedInTransaction += InsertBlockParent;
      blockchain.OnBlockRolledBackInTransaction += RollBackBlockParent;
    }

    internal abstract bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feeAnchor, long budget);

    internal abstract long GetCostAnchor(long feeAnchor);

    internal abstract long GetFunds();

    internal Dictionary<string, object> GetSnapshotState()
    {
      return new Dictionary<string, object>
      {
        ["funds"] = GetFunds(),
        ["addressFunding"] = Wallet.AddressAccountReceiving,
        ["anchors"] = AnchorsStored.FindAll().Select(documentAnchor =>
        {
          TX anchor = ParseAnchor(documentAnchor);
          int heightConfirmed = documentAnchor[NAME_FIELD_HEIGHT_CONFIRMED].AsInt32;

          Dictionary<string, object> snapshotAnchor = anchor.GetSnapshotState();
          snapshotAnchor["heightConfirmed"] = heightConfirmed;
          snapshotAnchor["isMissed"] = heightConfirmed == 0 && IsAnchorMissed(anchor);

          return snapshotAnchor;
        }).ToList()
      };
    }

    internal abstract long GetFeeAnchorMin();

    internal abstract long GetStepFeeRaise();

    internal abstract long GetStepFeeDecrease();

    protected abstract bool IsAnchorConfirmed(TX anchor, int height);

    protected abstract bool IsAnchorMissed(TX anchor);

    protected abstract void InsertBlockParentFunds(Block block);

    protected abstract void RollBackBlockParentFunds(Block block);

    void InsertBlockParent(Block block)
    {
      int height = block.Header.Height;

      InsertBlockParentFunds(block);

      foreach (BsonDocument documentAnchor in AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0)).ToList())
        if (IsAnchorConfirmed(ParseAnchor(documentAnchor), height))
        {
          documentAnchor[NAME_FIELD_HEIGHT_CONFIRMED] = height;
          AnchorsStored.Update(documentAnchor);

          Console.WriteLine($"Anchor TX {documentAnchor[NAME_FIELD_ID].AsBinary.ToHexString()} confirmed at {Blockchain.NameToken} height {height}.");
        }

      AnchorsStored.DeleteMany(Query.Between(NAME_FIELD_HEIGHT_CONFIRMED, 1, height - BITCOIN_REORG_MARGIN));
    }

    void RollBackBlockParent(Block block)
    {
      RollBackBlockParentFunds(block);

      foreach (BsonDocument documentAnchor in AnchorsStored.Find(Query.GTE(NAME_FIELD_HEIGHT_CONFIRMED, block.Header.Height)).ToList())
      {
        documentAnchor[NAME_FIELD_HEIGHT_CONFIRMED] = 0;
        AnchorsStored.Update(documentAnchor);
      }
    }

    protected TX ParseAnchor(BsonDocument documentAnchor)
    {
      byte[] tXRaw = documentAnchor[NAME_FIELD_TX_RAW].AsBinary;
      int index = 0;

      TX tX = Blockchain.Parser.ParseTX(tXRaw, ref index, isCoinbase: false, verifySignatures: false);

      tX.TXRaw = tXRaw;
      tX.Fee = documentAnchor[NAME_FIELD_FEE].AsInt64;

      return tX;
    }

    protected void InsertAnchor(TX tXAnchor)
    {
      AnchorsStored.Insert(new BsonDocument
      {
        [NAME_FIELD_ID] = tXAnchor.Hash,
        [NAME_FIELD_TX_RAW] = tXAnchor.TXRaw,
        [NAME_FIELD_FEE] = tXAnchor.Fee,
        [NAME_FIELD_HEIGHT_CONFIRMED] = 0,
        [NAME_FIELD_TIME_SENT] = DateTime.UtcNow.Ticks
      });
    }

    protected List<TX> LoadAnchorsUnconfirmed()
    {
      return AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
        .Select(ParseAnchor)
        .ToList();
    }

    internal int CountAnchorsMissed(DateTime timeSentLatest)
    {
      return AnchorsStored.Find(Query.EQ(NAME_FIELD_HEIGHT_CONFIRMED, 0))
        .Where(d => d[NAME_FIELD_TIME_SENT].AsInt64 <= timeSentLatest.Ticks)
        .Select(ParseAnchor)
        .Count(IsAnchorMissed);
    }
  }
}
