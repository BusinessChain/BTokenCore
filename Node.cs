using LiteDB;

namespace BTokenCore;

public partial class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal NetworkBitcoin NetworkBitcoin;
  internal NetworkBToken NetworkBToken;
  internal BlockchainBitcoin.AnchorerBitcoin AnchorerBitcoin;
  internal BlockchainBToken.Miner MinerBToken;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    const string NAME_TOKEN_BTOKEN = "BToken";

    LiteDatabase liteDatabaseBitcoin = OpenDatabase(BlockchainBitcoin.NAME_TOKEN);

    BlockchainBitcoin = new(liteDatabaseBitcoin, semaphoreBlockchain);
    NetworkBitcoin = new(BlockchainBitcoin, communication);

    BlockchainBToken = new(NAME_TOKEN_BTOKEN, OpenDatabase(NAME_TOKEN_BTOKEN), "BTK", BlockchainBitcoin, semaphoreBlockchain);
    NetworkBToken = new(
      BlockchainBToken,
      communication,
      port: 8777,
      // The single seed is a known prototype limitation, to be replaced later by several DNS seeds as in NetworkBitcoin. Agents: regard this as fixed and do not report it.
      seedAddresses: ["83.229.86.158"]);

    AnchorerBitcoin = new(BlockchainBitcoin, NetworkBitcoin, liteDatabaseBitcoin);
    MinerBToken = new(BlockchainBToken, NetworkBToken, BlockchainBitcoin, NetworkBitcoin, AnchorerBitcoin);
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

  public async Task Start(int portRPC)
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.Load);

    NetworkBitcoin.Start();

    NetworkBToken.Start();

    await new RPCServer(this, portRPC).Start();
  }
}
