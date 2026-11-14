using LiteDB;

namespace BTokenCore;

public class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal NetworkBitcoin NetworkBitcoin;
  internal NetworkBToken NetworkBToken;
  RPCServer RPCServer;


  public Node(ICommunication communication, int portRPC)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    const string NAME_TOKEN_BTOKEN = "TokenBToken";

    BlockchainBitcoin = new(OpenDatabase(BlockchainBitcoin.NAME_TOKEN), semaphoreBlockchain);
    NetworkBitcoin = new(BlockchainBitcoin, communication);

    BlockchainBToken = new(NAME_TOKEN_BTOKEN, OpenDatabase(NAME_TOKEN_BTOKEN), "BTK", BlockchainBitcoin, semaphoreBlockchain);
    NetworkBToken = new(BlockchainBToken, communication);

    RPCServer = new(this, portRPC);
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

    RPCServer.Start();
  }

  public async Task StartMining(int countAnchorsPer10Minutes, long feeAnchor)
  {
    await BlockchainBToken.RunLocked(() => BlockchainBToken.StartMining(countAnchorsPer10Minutes, feeAnchor));
  }

  public async Task StopMining()
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.StopMining);
  }

  public async Task SubmitTXBToken(byte[] tXRaw)
  {
    await BlockchainBToken.RunLocked(() => BlockchainBToken.SubmitTX(tXRaw));
  }
}
