namespace BTokenCore;

internal partial class BlockchainBToken : Blockchain
{
  protected class Miner
  {
    BlockchainBToken Blockchain;

    const int MILLISECONDS_WINDOW_BLANK = 5_000;

    Timer TimerMining;
    long Budget;
    uint NonceBlockNext;
    string StateRoundLast = "";


    internal Miner(BlockchainBToken blockchain)
    {
      Blockchain = blockchain;
      NonceBlockNext = (uint)Random.Shared.Next();
    }

    internal Dictionary<string, object> GetSnapshotState()
    {
      return new Dictionary<string, object>
      {
        ["isMining"] = TimerMining != null,
        ["budget"] = Budget,
        ["stateRoundLast"] = StateRoundLast
      };
    }

    internal void StartMining(long budget)
    {
      if (budget < BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR)
        throw new ProtocolException($"Budget {budget} must be at least {BlockchainBitcoin.FEE_SURCHARGE_REPLACEMENT_ANCHOR} satoshis.");

      Budget = budget;

      if (TimerMining != null)
        return;

      TimerMining = new Timer(
        _ => Blockchain.RunLocked(() => RunTimer(MineBlock)),
        null,
        0,
        Timeout.Infinite);
    }

    internal void StopMining()
    {
      TimerMining?.Dispose();
      TimerMining = null;
    }

    internal void ScheduleMining(bool isAnchorWinnerForeign)
    {
      TimerMining?.Change(isAnchorWinnerForeign ? MILLISECONDS_WINDOW_BLANK : 0, Timeout.Infinite);
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
        StateRoundLast = $"Mining stopped after {exception.GetType().Name}: {exception.Message}";
        Console.WriteLine($"Warning: {StateRoundLast}");
        StopMining();
      }
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
      int height = Blockchain.ChainRoot.HeaderTipBlockchain.Height + 1;
      string prefixSkip = $"Mining round at height {height} skipped: ";

      if (!Blockchain.Synchronizer.IsAccountsAtTip())
      {
        StateRoundLast = prefixSkip + "the accounts are not at the tip.";
        return;
      }

      if (!Blockchain.BlockchainParent.IsBlockTipAtHeaderTip())
      {
        StateRoundLast = prefixSkip + "the Bitcoin blocks are not at the Bitcoin header tip.";
        return;
      }

      Block block = new(Blockchain.SizeBlockMax);

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
        HashAnchors = ParserToken.ComputeHashAnchors(ParserToken.EnumerateAnchorsEligible(block.TXs, height)),
        Nonce = NonceBlockNext++
      };

      try
      {
        ((HeaderBToken)block.Header).HashAccounts = Blockchain.Accounts.ComputeHash(block);
      }
      catch (ProtocolException exception)
      {
        StateRoundLast = prefixSkip + exception.Message;
        Console.WriteLine(StateRoundLast);
        return;
      }

      block.Header.ComputeHash();

      TXOutputTokenAnchor anchorToken = new()
      {
        IDToken = Blockchain.IDToken,
        HashBlockPreviousReferenced = block.Header.HashPrevious,
        HashBlockReferenced = block.Header.Hash
      };

      if (!Blockchain.BlockchainParent.TryCreateTXAnchor(anchorToken, Budget, out int lockTimeAnchor))
      {
        StateRoundLast = prefixSkip + $"no Bitcoin wallet output funds the budget {Budget}.";
        return;
      }

      block.Serialize();

      Blockchain.StoreBlockAwaitingAnchor(block, lockTimeAnchor);

      StateRoundLast = $"Block at height {height} awaits its anchor.";
    }
  }
}
