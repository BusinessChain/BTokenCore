namespace BTokenCore;

public class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal Network NetworkBitcoin;
  internal Network NetworkBToken;
  internal Miner Miner;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    BlockchainBitcoin = new(
      new TokenBitcoin(),
      semaphoreBlockchain);

    ConfigNetwork ConfigNetworkBitcoin = new()
    {
      Port = 8333,
      IsProtocolBitcoin = true,
      NetworkServicesRemote = ConfigNetwork.NODE_NETWORK,
      SeedAddresses =
        [
          "seed.bitcoin.sipa.be",
          "dnsseed.bluematt.me",
          "dnsseed.bitcoin.dashjr.org",
          "seed.bitcoinstats.com",
          "seed.bitnodes.io"
        ]
    };

    NetworkBitcoin = new(
      BlockchainBitcoin,
      communication, 
      ConfigNetworkBitcoin);


    TokenBToken tokenBToken = new();

    BlockchainBToken = new(
      tokenBToken,
      blockchainParent: BlockchainBitcoin,
      semaphoreBlockchain);

    ConfigNetwork ConfigNetworkBToken = new()
    {
      Port = 8777,
      EnableInboundConnections = true,
      EnableRelay = true
    };

    NetworkBToken = new(
      BlockchainBToken,
      communication,
      ConfigNetworkBToken);

    Miner = new(BlockchainBitcoin, BlockchainBToken, NetworkBitcoin, NetworkBToken);

    BlockchainBitcoin.OnBlockInserted += BlockchainBToken.InsertHeaderPlaceholder;
    BlockchainBitcoin.OnBlockInserted += BlockchainBToken.TriggerBootstrap;
    BlockchainBitcoin.OnBlockInserted += Miner.OnBlockBitcoinInserted;

    BlockchainBitcoin.OnBlockRolledBack += BlockchainBToken.RemoveHeaderPlaceholder;

    BlockchainBToken.OnBlockInserted += tokenBToken.TXPool.RemoveTXs;
    BlockchainBToken.OnBlockRolledBack += tokenBToken.TXPool.ReturnTXs;
  }

  public void Start()
  {
    BlockchainBToken.LoadBlockchain();
    BlockchainBitcoin.LoadBlockchain();
    BlockchainBToken.RollBackUnanchored();
    NetworkBitcoin.Start();

    NetworkBToken.Start();
  }

  public async Task StartMining(long feePerByte)
  {
    await Miner.StartMining(feePerByte);
  }

  public void StopMining()
  {
    Miner.StopMining();
  }
}
