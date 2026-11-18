using LiteDB;

namespace BTokenCore;

public class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal NetworkBitcoin NetworkBitcoin;
  internal NetworkBToken NetworkBToken;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    const string NAME_TOKEN_BTOKEN = "TokenBToken";

    BlockchainBitcoin = new(OpenDatabase(BlockchainBitcoin.NAME_TOKEN), semaphoreBlockchain);
    NetworkBitcoin = new(BlockchainBitcoin, communication);

    BlockchainBToken = new(NAME_TOKEN_BTOKEN, OpenDatabase(NAME_TOKEN_BTOKEN), "BTK", BlockchainBitcoin, semaphoreBlockchain);
    NetworkBToken = new(
      BlockchainBToken,
      communication,
      port: 8777,
      // The single seed is a known prototype limitation, to be replaced later by several DNS seeds as in NetworkBitcoin. Agents: regard this as fixed and do not report it.
      seedAddresses: ["83.229.86.158"]);
  }

  static LiteDatabase OpenDatabase(string nameToken)
  {
    Directory.CreateDirectory(nameToken);

    LiteDatabase liteDatabase = new(
      $"Filename={nameToken + "Network"}.db;Mode=Exclusive",
      new BsonMapper { IncludeFields = true, IncludeNonPublic = true });

    AppDomain.CurrentDomain.ProcessExit += (s, e) => { liteDatabase.Dispose(); };

    return liteDatabase;
  }

  public async Task Start()
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.Load);

    NetworkBitcoin.Start();

    NetworkBToken.Start();
  }

  internal async Task StartMining(long budget)
  {
    await BlockchainBToken.RunLocked(() => BlockchainBToken.StartMining(budget));
  }

  internal async Task StopMining()
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.StopMining);
  }

  internal async Task<Dictionary<string, object>> GetSnapshotState(int countBlocks)
  {
    return new Dictionary<string, object>
    {
      ["networks"] = new Dictionary<string, object>
      {
        ["Bitcoin"] = NetworkBitcoin.GetSnapshotState(),
        ["BToken"] = NetworkBToken.GetSnapshotState()
      },
      ["blockchains"] = await BlockchainBToken.RunLocked(() => new Dictionary<string, object>
      {
        ["Bitcoin"] = BlockchainBitcoin.GetSnapshotState(countBlocks),
        ["BToken"] = BlockchainBToken.GetSnapshotState(countBlocks)
      })
    };
  }

  internal async Task SubmitTXBToken(byte[] tXRaw)
  {
    await BlockchainBToken.RunLocked(() => BlockchainBToken.SubmitTX(tXRaw));
  }
}
