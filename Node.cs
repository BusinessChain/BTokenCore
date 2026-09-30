namespace BTokenCore;

public class Node
{
  internal Network NetworkBitcoin;
  internal Network NetworkBToken;
  internal Miner Miner;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    NetworkBitcoin = new(
      communication,
      new TokenBitcoin(),
      headerRootParent: null,
      semaphoreBlockchain,
      flagEnableInboundConnections: false,
      flagEnableRelay: false);

    NetworkBToken = new(
      communication,
      new TokenBToken(),
      headerRootParent: NetworkBitcoin.BlockchainRoot.HeaderRoot,
      semaphoreBlockchain,
      flagEnableInboundConnections: true,
      flagEnableRelay: true);

    Miner = new(NetworkBitcoin, NetworkBToken);

    NetworkBitcoin.OnBlockInserted = Miner.OnBlockBitcoinInserted;
  }

  public void Start()
  {
    NetworkBitcoin.Start();
    NetworkBToken.Start();
  }
}
