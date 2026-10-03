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

    NetworkBitcoin = new(
      BlockchainBitcoin,
      communication,
      new ConfigNetwork
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
      });

    BlockchainBToken = new(
      new TokenBToken(),
      blockchainParent: BlockchainBitcoin,
      semaphoreBlockchain);

    NetworkBToken = new(
      BlockchainBToken,
      communication,
      new ConfigNetwork
      {
        Port = 8777,
        EnableInboundConnections = true,
        EnableRelay = true
      });

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
