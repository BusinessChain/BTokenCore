namespace BTokenCore;

public class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal Network NetworkBitcoin;
  internal Network NetworkBToken;
  internal Miner Miner;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    #region Bitcoin
    BlockchainBitcoin = new(semaphoreBlockchain);

    ConfigNetwork ConfigNetworkBitcoin = new()
    {
      Port = 8333,
      IsProtocolBitcoin = true,
      NetworkServicesRemote = ConfigNetwork.NODE_NETWORK,
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
    #endregion

    #region BToken
    BlockchainBToken = new(
      blockchainParent: BlockchainBitcoin,
      semaphoreBlockchain);

    ConfigNetwork ConfigNetworkBToken = new()
    {
      Port = 8777,
      EnableInboundConnections = true,
      EnableRelay = true
    };

    NetworkBToken = new(
      BlockchainBToken,
      communication,
      ConfigNetworkBToken);
    #endregion

    Miner = new(BlockchainBitcoin, BlockchainBToken, NetworkBitcoin);

    BlockchainBitcoin.OnBlockInserted += BlockchainBToken.InsertHeaderPlaceholder;
    BlockchainBitcoin.OnBlockInserted += BlockchainBToken.TriggerBootstrap;
    BlockchainBitcoin.OnBlockInserted += Miner.InsertBlockMined;

    BlockchainBitcoin.OnBlockRolledBack += BlockchainBToken.RemoveHeaderPlaceholder;

    BlockchainBToken.OnTipChanged += Miner.MineBlockNext;
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
    await Miner.StartMining(feePerByte);
  }

  public void StopMining()
  {
    Miner.StopMining();
  }

  public async Task SubmitTXBToken(byte[] tXRaw)
  {
    await NetworkBToken.SubmitTX(tXRaw);
  }
}
