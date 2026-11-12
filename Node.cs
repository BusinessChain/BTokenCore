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

  public async Task Start()
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.Load);

    NetworkBitcoin.Start();

    NetworkBToken.Start();
  }

  public async Task StartMining(int countAnchorsPer10Minutes, long feeAnchor)
  {
    await BlockchainBToken.RunLocked(() => BlockchainBToken.StartMining(countAnchorsPer10Minutes, feeAnchor));
  }

  public async Task StopMining()
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.StopMining);
  }

  public async Task SubmitTXBToken(byte[] tXRaw)
  {
    await BlockchainBToken.RunLocked(() => BlockchainBToken.SubmitTX(tXRaw));
  }
}
