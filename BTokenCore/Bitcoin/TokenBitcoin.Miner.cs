using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;


using BTokenLib;

namespace BTokenCore
{
  partial class TokenBitcoin : Token
  {
    const long BLOCK_REWARD_INITIAL = 5000000000; // 50 BTK
    const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

    const int COUNT_TXS_PER_BLOCK_MAX = 5;
    int NumberOfProcesses = 1;// Math.Max(Environment.ProcessorCount - 1, 1);


    public override void StartMining()
    {
      if (IsMining)
        return;

      IsMining = true;

      Parallel.For(
        0,
        NumberOfProcesses,
        i => RunMining(i));

      Console.WriteLine("Miner canceled.");
    }

    void RunMining(int indexThread)
    {
      $"Start {GetName()} miner on thread {indexThread}.".Log(LogFile);

      SHA256 sHA256 = SHA256.Create();

      while (true)
      {
        BlockBitcoin block = ComputePoW(sHA256, indexThread);

        if (!IsMining)
          return;

        block.Buffer = block.Header.Buffer.Concat(
          VarInt.GetBytes(block.TXs.Count)).ToArray();

        block.TXs.ForEach(t => { block.Buffer = block.Buffer.Concat(t.TXRaw).ToArray(); });

        block.Parse();

        while (!TryLock())
          Thread.Sleep(500);

        try
        {
          InsertBlock(block);

          Console.Beep();

          ($"Bitcoin Miner {indexThread} mined block height " +
            $"{block.Header.Height} with hash {block}.").Log(LogFile);

          Network.AdvertizeBlockToNetwork(block);
        }
        catch (Exception ex)
        {
          ($"{ex.GetType().Name} when when miner tries to insert mined bitcoin " +
            $"block height {block.Header.Height}, {block}:\n{ex.Message}.").Log(LogFile);

          continue;
        }
        finally
        {
          ReleaseLock();
        }
      }
    }

    BlockBitcoin ComputePoW(
      SHA256 sHA256,
      int indexThread)
    {
    LABEL_StartPoW:

      uint seed = (uint)(indexThread * uint.MaxValue / NumberOfProcesses);

      BlockBitcoin block = new();

      int height = HeaderTip.Height + 1;

      long blockReward = BLOCK_REWARD_INITIAL >>
        height / PERIOD_HALVENING_BLOCK_REWARD;

      TX tXCoinbase = CreateCoinbaseTX(block, height, blockReward);

      block.TXs.Add(tXCoinbase);
      block.TXs.AddRange(
        TXPool.GetTXs(out int countTXsPool, COUNT_TXS_PER_BLOCK_MAX));

      $"Bitcoin miner mines block with tXs:".Log(LogFile);
      block.TXs.ForEach(t => $"{t}".Log(LogFile));

      uint nBits = HeaderBitcoin.GetNextTarget((HeaderBitcoin)HeaderTip);
      double difficulty = HeaderBitcoin.ComputeDifficultyFromNBits(nBits);

      HeaderBitcoin header = new()
      {
        Version = 0x01,
        HashPrevious = HeaderTip.Hash,
        HeaderPrevious = HeaderTip,
        Height = height,
        UnixTimeSeconds = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
        Nonce = seed,
        NBits = nBits,
        Difficulty = difficulty,
        DifficultyAccumulated = HeaderTip.DifficultyAccumulated + difficulty,
        MerkleRoot = block.ComputeMerkleRoot()
      };

      block.Header = header;

      header.ComputeHash(sHA256);

      while (header.Hash.IsGreaterThan(header.NBits))
      {
        if (HeaderTip.Height >= height
          || TXPool.GetCountTXs() != countTXsPool)
          goto LABEL_StartPoW;

        if (!IsMining)
          break;

        header.IncrementNonce(seed);
        header.ComputeHash(sHA256);
      }

      return block;
    }
  }
}
