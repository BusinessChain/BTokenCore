namespace BTokenCore;

public class Node
{
  internal Blockchain BlockchainBitcoin;
  internal Blockchain BlockchainBToken;
  internal Miner Miner;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    BlockchainBitcoin = new(
      communication,
      new TokenBitcoin(),
      headerRootParent: null,
      semaphoreBlockchain,
      flagEnableInboundConnections: false,
      flagEnableRelay: false);

    BlockchainBToken = new(
      communication,
      new TokenBToken(),
      headerRootParent: BlockchainBitcoin.BlockchainRoot.HeaderRoot,
      semaphoreBlockchain,
      flagEnableInboundConnections: true,
      flagEnableRelay: true);

    Miner = new(BlockchainBitcoin, BlockchainBToken);

    BlockchainBitcoin.OnBlockInserted = Miner.OnBlockBitcoinInserted;
  }

  public void Start()
  {
    BlockchainBitcoin.Start();
    BlockchainBToken.Start();
  }
}
