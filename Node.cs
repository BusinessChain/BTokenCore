namespace BTokenCore;

public class Node
{
  internal Network NetworkBitcoin;
  internal Network NetworkBToken;


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

    NetworkBitcoin.OnBlockInserted = NetworkBToken.OnBlockParentInserted;

    NetworkBToken.OnTokenAnchorMined = NetworkBitcoin.MineTokenAnchor;
  }

  public void Start()
  {
    NetworkBitcoin.Start();
    NetworkBToken.Start();
  }
}
