namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class Miner
  {
    BlockchainBToken Blockchain;

    const int MILLISECONDS_PER_10_MINUTES = 600_000;

    Timer TimerMining;
    int CountAnchorsPer10Minutes;
    long FeeAnchor;
    uint NonceBlockNext;


    internal Miner(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
    }

    internal void StartMining(int countAnchorsPer10Minutes, long feeAnchor)
    {
      CountAnchorsPer10Minutes = countAnchorsPer10Minutes;
      FeeAnchor = feeAnchor;

      TimerMining ??= new Timer(
        _ => Blockchain.RunLocked(Tick),
        null,
        MILLISECONDS_PER_10_MINUTES / CountAnchorsPer10Minutes,
        Timeout.Infinite);
    }

    internal void StopMining()
    {
      TimerMining?.Dispose();
      TimerMining = null;
    }

    void Tick()
    {
      if (TimerMining == null)
        return;

      MineBlock();

      TimerMining.Change(MILLISECONDS_PER_10_MINUTES / CountAnchorsPer10Minutes, Timeout.Infinite);
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

    void MineBlock()
    {
      if (!Blockchain.Synchronizer.IsAccountsAtTip())
        return;

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
        MerkleRoot = block.ComputeMerkleRoot(),
        Nonce = NonceBlockNext++
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

      if (!Blockchain.BlockchainParent.TryCreateTXAnchor(anchorToken, FeeAnchor))
        return;

      block.Serialize();

      Blockchain.StoreBlockAwaitingAnchor(block);
    }
  }
}
