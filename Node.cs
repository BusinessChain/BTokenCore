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
      headerRootParent: null,
      semaphoreBlockchain);

    BlockchainBToken = new(
      new TokenBToken(),
      headerRootParent: BlockchainBitcoin.BlockchainRoot.HeaderRoot,
      semaphoreBlockchain);

    NetworkBitcoin = new(
      BlockchainBitcoin,
      communication,
      BlockchainBitcoin.Token,
      flagEnableInboundConnections: false,
      flagEnableRelay: false);

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
    BlockchainBToken.LoadBlockchain();

    NetworkBitcoin.Start();
    NetworkBToken.Start();
  }
}
