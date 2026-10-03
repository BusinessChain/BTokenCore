namespace BTokenCore;

public class Node
{
  internal Blockchain BlockchainBitcoin;
  internal Blockchain BlockchainBToken;
  internal Network NetworkBitcoin;
  internal Network NetworkBToken;
  internal Miner Miner;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    BlockchainBitcoin = new(
      new TokenBitcoin(),
      blockchainParent: null,
      semaphoreBlockchain);

    ConfigNetwork ConfigNetworkBitcoin = new()
    {
      Port = 8333,
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


    ConfigNetwork ConfigNetworkBToken = new()
    {
      Port = 8777,
      EnableInboundConnections = true,
      EnableRelay = true
    };

    BlockchainBToken = new(
      new TokenBToken(),
      blockchainParent: BlockchainBitcoin,
      semaphoreBlockchain);

    NetworkBToken = new(
      BlockchainBToken,
      communication,
      ConfigNetworkBToken);

    Miner = new(BlockchainBitcoin, BlockchainBToken, NetworkBitcoin, NetworkBToken);

    BlockchainBitcoin.OnBlockInserted = Miner.OnBlockBitcoinInserted;
  }

  public void Start()
  {
    BlockchainBitcoin.LoadBlockchain();
    NetworkBitcoin.Start();

    BlockchainBToken.LoadBlockchain();
    NetworkBToken.Start();
  }
}
