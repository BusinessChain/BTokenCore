namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  class Miner
  {
    BlockchainBToken Blockchain;

    const int MILLISECONDS_PER_10_MINUTES = 600_000;
    const int MILLISECONDS_WINDOW_BLANK = 5_000;

    Timer TimerMining;
    Timer TimerReplacementAnchorsMissed;
    int CountAnchorsPer10Minutes;
    long FeeAnchor;
    uint NonceBlockNext;
    DateTime TimeWindowBlankEnd;


    internal Miner(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
      NonceBlockNext = (uint)Random.Shared.Next();
    }

    internal void StartMining(int countAnchorsPer10Minutes, long feeAnchor)
    {
      CountAnchorsPer10Minutes = countAnchorsPer10Minutes;
      FeeAnchor = feeAnchor;

      if (TimerMining != null)
        return;

      TimerMining = new Timer(
        _ => Blockchain.RunLocked(() => RunTimer(Tick)),
        null,
        MILLISECONDS_PER_10_MINUTES / CountAnchorsPer10Minutes,
        Timeout.Infinite);

      TimerReplacementAnchorsMissed = new Timer(
        _ => Blockchain.RunLocked(() => RunTimer(ReplaceAnchorsMissed)),
        null,
        Timeout.Infinite,
        Timeout.Infinite);
    }

    internal void StopMining()
    {
      TimerMining?.Dispose();
      TimerMining = null;

      TimerReplacementAnchorsMissed?.Dispose();
      TimerReplacementAnchorsMissed = null;
    }

    internal void ScheduleReplacementAnchorsMissed(bool isAnchorWinnerForeign)
    {
      if (TimerMining == null)
        return;

      if (isAnchorWinnerForeign)
      {
        TimeWindowBlankEnd = DateTime.UtcNow.AddMilliseconds(MILLISECONDS_WINDOW_BLANK);
        TimerReplacementAnchorsMissed.Change(MILLISECONDS_WINDOW_BLANK, Timeout.Infinite);
      }
      else
        TimerReplacementAnchorsMissed.Change(0, Timeout.Infinite);
    }

    void RunTimer(Action action)
    {
      if (TimerMining == null)
        return;

      try
      {
        action();
      }
      catch (Exception exception)
      {
        Console.WriteLine($"Warning: mining stopped after {exception.GetType().Name}: {exception.Message}");
        StopMining();
      }
    }

    void ReplaceAnchorsMissed()
    {
      int countAnchorsReplaceable = Blockchain.BlockchainParent.CountAnchorsReplaceable(FeeAnchor);

      for (int i = 0; i < countAnchorsReplaceable; i++)
        MineBlock();
    }

    void Tick()
    {
      if (DateTime.UtcNow < TimeWindowBlankEnd)
      {
        TimerMining.Change(MILLISECONDS_WINDOW_BLANK, Timeout.Infinite);
        return;
      }

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

      if (!Blockchain.BlockchainParent.TryCreateTXAnchor(anchorToken, FeeAnchor, out int lockTimeAnchor))
        return;

      block.Serialize();

      Blockchain.StoreBlockAwaitingAnchor(block, lockTimeAnchor);
    }
  }
}
