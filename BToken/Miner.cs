namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class Miner
  {
    BlockchainBToken Blockchain;

    bool IsMining;
    long FeePerByte;
    bool IsRoundMiningOpen;
    byte[] HashBlockAwaitedRoundMining;


    internal Miner(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
    }

    internal void StartMining(long feePerByte)
    {
      // The user has to define the fee rate at which they want to pay for the anchoring.
      // The GUI could also offer a tool that controls the fee rate automatically,
      // e.g. based on past fee rates or market price arbitrage.

      FeePerByte = feePerByte;
      IsMining = true;
    }

    internal void StopMining()
    {
      IsMining = false;
    }

    internal void OpenRound(byte[] hashBlockAwaited)
    {
      IsRoundMiningOpen = true;
      HashBlockAwaitedRoundMining = hashBlockAwaited;
    }

    TXBToken CreateTXCoinbase(int height, long fees, long rent)
    {
      TXOutputP2PKH outputCoinbase = new(GetBlockReward(height) + fees + rent, Blockchain.Wallet.GetHash160KeyReceiving(Blockchain.TXPool.CanReceive));

      TXBToken tXCoinbase = new()
      {
        HeightAccountCreated = height
      };

      tXCoinbase.TXOutputs.Add(outputCoinbase);

      tXCoinbase.SerializeUnsigned();

      return tXCoinbase;
    }

    internal void MineBlock()
    {
      if (!IsMining || !Blockchain.Synchronizer.IsAccountsAtTip())
        return;

      bool isTipAwaited = Blockchain.ChainRoot.HeaderTipBlockchain.Hash.IsAllBytesEqual(HashBlockAwaitedRoundMining);

      if (!IsRoundMiningOpen && !isTipAwaited)
        return;

      IsRoundMiningOpen = false;

      if (isTipAwaited)
        HashBlockAwaitedRoundMining = null;

      Block block = new(Blockchain.SizeBlockMax);

      int height = Blockchain.ChainRoot.HeaderTipBlockchain.Height + 1;

      int countBytesTXsMax = block.Buffer.Length
        - HeaderBToken.COUNT_HEADER_BYTES
        - VarInt.GetBytes(int.MaxValue).Length
        - TXBToken.LENGTH_TX_COINBASE;

      block.TXs = Blockchain.TXPool.GetTXs(countBytesTXsMax);

      block.TXs.Insert(0, CreateTXCoinbase(height, block.TXs.Sum(t => t.Fee), Blockchain.Accounts.LoadRentSweep(height)));

      block.Header = new HeaderBToken()
      {
        Height = height,
        HashPrevious = Blockchain.ChainRoot.HeaderTipBlockchain.Hash,
        MerkleRoot = block.ComputeMerkleRoot()
      };

      try
      {
        ((HeaderBToken)block.Header).HashAccounts = Blockchain.Accounts.ComputeHash(block);
      }
      catch (ProtocolException exception)
      {
        Console.WriteLine($"Mining round at height {height} skipped: {exception.Message}");
        return;
      }

      block.Header.ComputeHash();

      TXOutputTokenAnchor anchorToken = new()
      {
        IDToken = Blockchain.IDToken,
        HashBlockPreviousReferenced = block.Header.HashPrevious,
        HashBlockReferenced = block.Header.Hash
      };

      if (!Blockchain.BlockchainParent.TryCreateTXAnchor(anchorToken, FeePerByte))
        return;

      block.Serialize();

      Blockchain.StoreBlockAwaitingAnchor(block);
    }
  }
}
