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
      null,
      semaphoreBlockchain,
      flagEnableInboundConnections: false,
      flagEnableRelay: false);

    NetworkBToken = new(
      communication,
      new TokenBToken(),
      NetworkBitcoin.BlockchainRoot.HeaderRoot,
      semaphoreBlockchain,
      flagEnableInboundConnections: true,
      flagEnableRelay: true);

    NetworkBitcoin.OnBlockInserted = block =>
    {
      block.Header.AnchorsWinner.TryGetValue(NetworkBToken.Token.IDToken, out TXOutputTokenAnchor anchorWinner);
      NetworkBToken.OnBlockParentInserted(anchorWinner);
    };

    NetworkBToken.OnTokenAnchorMined = NetworkBitcoin.MineTokenAnchor;
  }

  public void Start()
  {
    NetworkBitcoin.Start();
    NetworkBToken.Start();
  }
}
