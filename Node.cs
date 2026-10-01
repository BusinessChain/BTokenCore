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
      BlockchainBitcoin.Token,
      flagEnableInboundConnections: false,
      flagEnableRelay: false);

    BlockchainBToken = new(
      new TokenBToken(),
      blockchainParent: BlockchainBitcoin,
      semaphoreBlockchain);

    NetworkBToken = new(
      BlockchainBToken,
      communication,
      BlockchainBToken.Token,
      flagEnableInboundConnections: true,
      flagEnableRelay: true);

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
