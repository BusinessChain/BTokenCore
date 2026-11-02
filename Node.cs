namespace BTokenCore;

public class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal NetworkBitcoin NetworkBitcoin;
  internal NetworkBToken NetworkBToken;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    BlockchainBitcoin = new(semaphoreBlockchain);
    NetworkBitcoin = new(BlockchainBitcoin, communication);

    BlockchainBToken = new(BlockchainBitcoin, semaphoreBlockchain);
    NetworkBToken = new(BlockchainBToken, communication);
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
    await BlockchainBToken.StartMining(feePerByte);
  }

  public void StopMining()
  {
    BlockchainBToken.StopMining();
  }

  public async Task SubmitTXBToken(byte[] tXRaw)
  {
    await NetworkBToken.SubmitTX(tXRaw);
  }
}
