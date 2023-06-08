using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;

using BTokenLib;

namespace BTokenCore
{
  partial class TokenBToken : Token
  {
    const int COUNT_TXS_PER_BLOCK_MAX = 5;

    const long BLOCK_REWARD_INITIAL = 200000000000000; // 200 BTK
    const int PERIOD_HALVENING_BLOCK_REWARD = 105000;

    const int TIMESPAN_MINING_LOOP_MILLISECONDS = 1 * 5000;
    const double FACTOR_INCREMENT_FEE_PER_BYTE = 1.2;

    const int SIZE_BUFFER_BLOCK = 0x400000;

    const int LENGTH_DATA_ANCHOR_TOKEN = 66;
    const int LENGTH_DATA_P2PKH_INPUT = 180;
    const int LENGTH_DATA_TX_SCAFFOLD = 10;
    const int LENGTH_DATA_P2PKH_OUTPUT = 34;

    readonly byte[] ID_BTOKEN = { 0x01, 0x00 };

    const long COUNT_SATOSHIS_PER_DAY_MINING = 500000;
    const long TIMESPAN_DAY_SECONDS = 24 * 3600;

    const UInt16 COMPORT_BTOKEN = 8777;

    string PathBlocksMinedUnconfirmed;

    DatabaseAccounts DatabaseAccounts;
 
    Dictionary<byte[], int> WinningBlockInHeightAnchorBlock = 
      new(new EqualityComparerByteArray());

    List<BlockBToken> BlocksMined = new();




    public TokenBToken(Token tokenParent)
      : base(
          COMPORT_BTOKEN,
          flagEnableInboundConnections: true)
    {
      TokenParent = tokenParent;
      tokenParent.TokenChild = this;

      DatabaseAccounts = new();

      PathBlocksMinedUnconfirmed = Path.Combine(
        GetName(),
        "BlocksMinedUnconfirmed");

      Directory.CreateDirectory(PathBlocksMinedUnconfirmed);

      foreach (string pathFile in Directory.GetFiles(PathBlocksMinedUnconfirmed))
      {
        try
        {
          BlockBToken block = new();
          block.Parse(File.ReadAllBytes(pathFile));
          BlocksMined.Add(block);

          //TokensAnchorMinedUnconfirmed.Add(tokenAnchor);
        }
        catch(Exception ex)
        {
          $"Failed to parse unconfirmed mined block {pathFile}.\n{ex.Message}".Log(LogFile);
        }
      }
    }

    public override Header CreateHeaderGenesis()
    {
      HeaderBToken header = new(
        headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
        hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
        merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
        unixTimeSeconds: 1231006505,
        nonce: 0);

      header.DifficultyAccumulated = header.Difficulty;

      return header;
    }

    public override void LoadImageDatabase(string pathImage)
    {
      DatabaseAccounts.LoadImage(pathImage);
    }

    public override void LoadImageHeaderchain(
      string pathImage,
      int heightMax)
    {
      byte[] winningBlockInHeightAnchorBlock = File.ReadAllBytes(
        Path.Combine(pathImage, "winningBlockInHeightAnchorBlock"));

      WinningBlockInHeightAnchorBlock.Clear();
      int i = 0;

      while (i < winningBlockInHeightAnchorBlock.Length)
      {
        byte[] hashblock = new byte[32];
        Array.Copy(winningBlockInHeightAnchorBlock, i, hashblock, 0, 32);
        i += 32;

        int height = BitConverter.ToInt32(winningBlockInHeightAnchorBlock, i);
        i += 4;

        WinningBlockInHeightAnchorBlock.Add(hashblock, height);
      }

      base.LoadImageHeaderchain(pathImage, heightMax);
    }


    public override void CreateImageHeaderchain(string path)
    {
      base.CreateImageHeaderchain(path);

      using (FileStream fileWinningBlockInHeightAnchorBlock = new(
          Path.Combine(path, "winningBlockInHeightAnchorBlock"),
          FileMode.Create,
          FileAccess.Write,
          FileShare.None))
      {
        foreach (KeyValuePair<byte[], int> keyValuePair in WinningBlockInHeightAnchorBlock)
        {
          fileWinningBlockInHeightAnchorBlock.Write(
            keyValuePair.Key, 0, keyValuePair.Key.Length);

          byte[] heightBytes = BitConverter.GetBytes(keyValuePair.Value);
          fileWinningBlockInHeightAnchorBlock.Write(heightBytes, 0, heightBytes.Length);
        }
      }
    }

    public override void CreateImageDatabase(string pathImage)
    {
      DatabaseAccounts.CreateImage(pathImage);
    }


    public override void StartMining()
    {
      if (IsMining)
        return;

      IsMining = true;

      "Start BToken miner".Log(LogFile);

      RunMining();
    }


    SHA256 SHA256Miner = SHA256.Create();
    Random RandomGeneratorMiner = new();

    const double FEE_SATOSHI_PER_BYTE_INITIAL = 1.0;
    double FeeSatoshiPerByte;

    List<TokenAnchor> TokensAnchorUnconfirmed = new();

    async Task RunMining()
    {
      FeeSatoshiPerByte = FEE_SATOSHI_PER_BYTE_INITIAL; // TokenParent.FeePerByteAverage;

      $"Miners starts with fee per byte = {FeeSatoshiPerByte}".Log(LogFile);

      while (IsMining)
      {
        int timeMSLoop = TIMESPAN_MINING_LOOP_MILLISECONDS;

        if (TryLock())
        {
          if (TryMineAnchorToken(out TokenAnchor tokenAnchor))
          {
            // timeMSLoop = (int)(tokenAnchor.TX.Fee * TIMESPAN_DAY_SECONDS * 1000 /
            // COUNT_SATOSHIS_PER_DAY_MINING);

            // timeMSLoop = RandomGeneratorMiner.Next(
            // timeMSCreateNextAnchorToken / 2,
            // timeMSCreateNextAnchorToken * 3 / 2);
          }

          ReleaseLock();
        }

        await Task.Delay(timeMSLoop).ConfigureAwait(false);
      }

      $"Exit BToken miner.".Log(LogFile);
    }

    int NumberSequence;

    bool TryMineAnchorToken(out TokenAnchor tokenAnchor)
    {
      long feeAccrued = (long)(FeeSatoshiPerByte * LENGTH_DATA_TX_SCAFFOLD);
      long feeAnchorToken = (long)(FeeSatoshiPerByte * LENGTH_DATA_ANCHOR_TOKEN);
      long feePerInput = (long)(FeeSatoshiPerByte * LENGTH_DATA_P2PKH_INPUT);
      long feeOutputChange = (long)(FeeSatoshiPerByte * LENGTH_DATA_P2PKH_OUTPUT);

      long valueAccrued = 0;

      tokenAnchor = new();
      tokenAnchor.NumberSequence = NumberSequence;
      tokenAnchor.IDToken = ID_BTOKEN;

      while (
        tokenAnchor.Inputs.Count < VarInt.PREFIX_UINT16 - 1 &&
        TokenParent.Wallet.TryGetOutput(
          feePerInput,
          out TXOutputWallet output))
      {
        tokenAnchor.Inputs.Add(output);
        valueAccrued += output.Value;

        feeAccrued += feePerInput;
      }

      feeAccrued += feeAnchorToken;

      if (valueAccrued < feeAccrued)
      {
        ($"Miner wallet has not enough value {valueAccrued} to " +
          $"pay for anchor token fee {feeAccrued}").Log(LogFile);

        return false;
      }

      BlockBToken block = new();

      int height = HeaderTip.Height + 1;

      long blockReward = BLOCK_REWARD_INITIAL >>
        height / PERIOD_HALVENING_BLOCK_REWARD;

      TX tXCoinbase = CreateCoinbaseTX(block, height, blockReward);

      block.TXs.Add(tXCoinbase);
      block.TXs.AddRange(
        TXPool.GetTXs(out int countTXsPool, COUNT_TXS_PER_BLOCK_MAX));

      HeaderBToken header = new()
      {
        HashPrevious = HeaderTip.Hash,
        HeaderPrevious = HeaderTip,
        Height = height,
        UnixTimeSeconds = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
        MerkleRoot = block.ComputeMerkleRoot()
      };

      block.Header = header;

      header.ComputeHash(SHA256Miner);

      block.Buffer = block.Header.Buffer.Concat(
        VarInt.GetBytes(block.TXs.Count)).ToArray();

      block.TXs.ForEach(t => { block.Buffer = block.Buffer.Concat(t.TXRaw).ToArray(); });

      block.Header.CountBytesBlock = block.Buffer.Length;

      tokenAnchor.HashBlockReferenced = block.Header.Hash;
      tokenAnchor.HashBlockPreviousReferenced = block.Header.HashPrevious;
      tokenAnchor.ValueChange = valueAccrued - feeAccrued - feeOutputChange;
      tokenAnchor.Serialize(TokenParent, SHA256Miner);

      if (tokenAnchor.ValueChange > 0)
        TokenParent.Wallet.AddOutput(
          new TXOutputWallet
          {
            TXID = tokenAnchor.TX.Hash,
            TXIDShort = tokenAnchor.TX.TXIDShort,
            Index = 1,
            Value = tokenAnchor.ValueChange
          });
      
      string pathFileBlock = Path.Combine(
        PathBlocksMinedUnconfirmed, 
        block.ToString());

      // File.WriteAllBytes(pathFileBlock, block.Buffer);

      BlocksMined.Add(block);
      TokensAnchorUnconfirmed.Add(tokenAnchor);

      // Immer bevor ein Token an einen Peer advertized wird,
      // fragt man den Peer ob er die Ancestor TX schon hat.
      // Wenn nicht iterativ weiterfragen und dann alle Tokens schicken.

      TokenParent.BroadcastTX(tokenAnchor.TX);

      ($"BToken miner successfully mined anchor Token {tokenAnchor.TX} with fee {tokenAnchor.TX.Fee}.\n" +
        $"{TokensAnchorUnconfirmed.Count} mined unconfirmed anchor tokens.")
        .Log(LogFile);

      return true;
    }

    List<TokenAnchor> TokensAnchorDetectedInBlock = new();

    public override void DetectAnchorTokenInBlock(TX tX)
    {
      TokenAnchor tokenAnchor = TokensAnchorUnconfirmed
        .Find(t => t.TX.Hash.IsEqual(tX.Hash));

      if (tokenAnchor != null)
      {
        $"Detected self mined anchor token {tX} in Bitcoin block".Log(LogFile);

        TokensAnchorUnconfirmed.Remove(tokenAnchor);
      }
      else
      {
        TXOutput tXOutput = tX.TXOutputs[0];

        int index = tXOutput.StartIndexScript;

        if (tXOutput.Buffer[index] != 0x6A)
          return;

        index += 1;

        if (tXOutput.Buffer[index] != LENGTH_DATA_ANCHOR_TOKEN)
          return;

        index += 1;

        if (!ID_BTOKEN.IsEqual(tXOutput.Buffer, index))
          return;

        $"Detected foreign - mined anchor token {tX} in Bitcoin block".Log(LogFile);

        index += ID_BTOKEN.Length;

        tokenAnchor = new(tX, index);
      }

      tokenAnchor.IsConfirmed = true;
      TokensAnchorDetectedInBlock.Add(tokenAnchor);

      $"Anchor token references {tokenAnchor.HashBlockReferenced.ToHexString()}".Log(LogFile);
    }

    public override void SignalCompletionBlockInsertion(Header headerParent)
    {
      try
      {
        $"{TokensAnchorDetectedInBlock.Count} anchor tokens detected in Bitcoin block."
          .Log(LogFile);

        if (TokensAnchorDetectedInBlock.Count > 0)
        {
          TokenAnchor tokenAnchorWinner = GetTXAnchorWinner(headerParent.Hash);

          ($"The winning anchor token is {tokenAnchorWinner.TX} referencing block " +
            $"{tokenAnchorWinner.HashBlockReferenced.ToHexString()}.").Log(LogFile);

          WinningBlockInHeightAnchorBlock.Add(
            tokenAnchorWinner.HashBlockReferenced,
            headerParent.Height);

          if (BlocksMined.Count > 0)
          {
            BlockBToken blockMined = BlocksMined.Find(b =>
            b.Header.Hash.IsEqual(tokenAnchorWinner.HashBlockReferenced));

            if (blockMined != null)
              InsertBlock(blockMined);

            if (TokensAnchorUnconfirmed.Count == 0)
            {
              NumberSequence = 0;
              FeeSatoshiPerByte /= FACTOR_INCREMENT_FEE_PER_BYTE;
            }
          }
        }

        TokensAnchorDetectedInBlock.Clear();
        BlocksMined.Clear();

        if (TokensAnchorUnconfirmed.Count > 0)
        {
          FeeSatoshiPerByte *= FACTOR_INCREMENT_FEE_PER_BYTE;

          $"{TokensAnchorUnconfirmed.Count} anchor tokens unconfirmed, do RBF."
            .Log(LogFile);

          NumberSequence += 1;

          RBFAnchorTokens();
        }

        $"New fee per byte is {FeeSatoshiPerByte}.".Log(LogFile);
      }
      catch(Exception ex)
      {
        ($"{ex.GetType().Name} when signaling Bitcoin block {headerParent}" +
          $" with height {headerParent.Height} to BToken.\n" +
          $"Exception message: {ex.Message}").Log(this, LogFile);
      }
    }

    TokenAnchor GetTXAnchorWinner(byte[] hashBlockAnchor)
    {
      SHA256 sHA256 = SHA256.Create();

      byte[] targetValue = sHA256.ComputeHash(hashBlockAnchor);
      byte[] biggestDifferenceTemp = new byte[32];
      TokenAnchor tokenAnchorWinner = null;

      TokensAnchorDetectedInBlock.ForEach(t =>
      {
        byte[] differenceHash = targetValue.SubtractByteWise(
          t.HashBlockReferenced);

        if (differenceHash.IsGreaterThan(biggestDifferenceTemp))
        {
          biggestDifferenceTemp = differenceHash;
          tokenAnchorWinner = t;
        }
      });

      return tokenAnchorWinner;
    }

    void RBFAnchorTokens()
    {
      TokensAnchorUnconfirmed.Reverse();

      foreach (TokenAnchor t in TokensAnchorUnconfirmed)
      {
        if (t.ValueChange > 0)
          TokenParent.Wallet.RemoveOutput(t.TX.Hash);

        t.Inputs.ForEach(i => TokenParent.Wallet.AddOutput(i));

        File.Delete(Path.Combine(
          PathBlocksMinedUnconfirmed,
          t.HashBlockReferenced.ToHexString()));
      }

      int countTokensAnchorUnconfirmed = TokensAnchorUnconfirmed.Count;
      TokensAnchorUnconfirmed.Clear();

      $"RBF {countTokensAnchorUnconfirmed} anchorTokens".Log(LogFile);

      while (countTokensAnchorUnconfirmed-- > 0)
        if (!TryMineAnchorToken(out TokenAnchor tokenAnchor))
          break;
    }

    protected override void InsertInDatabase(Block block)
    {
      $"Insert BToken block {block} in database.".Log(LogFile);

      DatabaseAccounts.InsertBlock((BlockBToken)block);

      long outputValueTXCoinbase = 0;
      block.TXs[0].TXOutputs.ForEach(o => outputValueTXCoinbase += o.Value);

      long blockReward = BLOCK_REWARD_INITIAL >>
        block.Header.Height / PERIOD_HALVENING_BLOCK_REWARD;

      if (blockReward + block.Fee != outputValueTXCoinbase)
        throw new ProtocolException(
          $"Output value of Coinbase TX {block.TXs[0]}\n" +
          $"does not add up to block reward {blockReward} plus block fee {block.Fee}.");
    }

    public override void InsertDB(
      byte[] bufferDB,
      int lengthDataInBuffer)
    {
      DatabaseAccounts.InsertDB(bufferDB, lengthDataInBuffer);
    }

    public override void DeleteDB()
    { 
      DatabaseAccounts.Delete(); 
    }
        
    public override void RevokeBlockInsertion()
    {
      TokensAnchorDetectedInBlock.Clear();
    }

    public override List<byte[]> ParseHashesDB(
      byte[] buffer,
      int length,
      Header headerTip)
    {
      SHA256 sHA256 = SHA256.Create();

      byte[] hashRootHashesDB = sHA256.ComputeHash(
        buffer,
        0,
        length);

      if (!((HeaderBToken)headerTip).HashDatabase.IsEqual(hashRootHashesDB))
        throw new ProtocolException(
          $"Root hash of hashesDB not equal to database hash in header tip");

      List<byte[]> hashesDB = new();

      for (
        int i = 0;
        i < DatabaseAccounts.COUNT_CACHES + DatabaseAccounts.COUNT_FILES_DB;
        i += 32)
      {
        byte[] hashDB = new byte[32];
        Array.Copy(buffer, i, hashDB, 0, 32);
        hashesDB.Add(hashDB);
      }

      return hashesDB;
    }

    public override Header ParseHeader(
        byte[] buffer,
        ref int index)
    {
      BlockBToken bTokenBlock = new();

      Header header = bTokenBlock.ParseHeader(
        buffer,
        ref index);

      if (!WinningBlockInHeightAnchorBlock.TryGetValue(header.Hash, out int heightBlockAnchor))
      {
        throw new ProtocolException($"Header {header} not anchored in parent chain.");
      }
      else if (header.Height > 1 && heightBlockAnchor < WinningBlockInHeightAnchorBlock[header.HashPrevious])
        throw new ProtocolException(
          $"Header {header} is anchored prior to its previous header {header.HeaderPrevious} in parent chain.");

      return header;
    }

    public override Block CreateBlock()
    {
      return new BlockBToken(SIZE_BUFFER_BLOCK);
    }

    byte[] GetGenesisBlockBytes()
    {
      return new byte[285]{
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x3b, 0xa3, 0xed, 0xfd, 0x7a, 0x7b, 0x12, 0xb2, 0x7a, 0xc7, 0x2c, 0x3e,
        0x67, 0x76, 0x8f, 0x61, 0x7f, 0xc8, 0x1b, 0xc3, 0x88, 0x8a, 0x51, 0x32, 0x3a, 0x9f, 0xb8, 0xaa,
        0x4b, 0x1e, 0x5e, 0x4a, 0x29, 0xab, 0x5f, 0x49, 0xff, 0xff, 0x00, 0x1d, 0x1d, 0xac, 0x2b, 0x7c,
        0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x4d, 0x04, 0xff, 0xff, 0x00, 0x1d,
        0x01, 0x04, 0x45, 0x54, 0x68, 0x65, 0x20, 0x54, 0x69, 0x6d, 0x65, 0x73, 0x20, 0x30, 0x33, 0x2f,
        0x4a, 0x61, 0x6e, 0x2f, 0x32, 0x30, 0x30, 0x39, 0x20, 0x43, 0x68, 0x61, 0x6e, 0x63, 0x65, 0x6c,
        0x6c, 0x6f, 0x72, 0x20, 0x6f, 0x6e, 0x20, 0x62, 0x72, 0x69, 0x6e, 0x6b, 0x20, 0x6f, 0x66, 0x20,
        0x73, 0x65, 0x63, 0x6f, 0x6e, 0x64, 0x20, 0x62, 0x61, 0x69, 0x6c, 0x6f, 0x75, 0x74, 0x20, 0x66,
        0x6f, 0x72, 0x20, 0x62, 0x61, 0x6e, 0x6b, 0x73, 0xff, 0xff, 0xff, 0xff, 0x01, 0x00, 0xf2, 0x05,
        0x2a, 0x01, 0x00, 0x00, 0x00, 0x43, 0x41, 0x04, 0x67, 0x8a, 0xfd, 0xb0, 0xfe, 0x55, 0x48, 0x27,
        0x19, 0x67, 0xf1, 0xa6, 0x71, 0x30, 0xb7, 0x10, 0x5c, 0xd6, 0xa8, 0x28, 0xe0, 0x39, 0x09, 0xa6,
        0x79, 0x62, 0xe0, 0xea, 0x1f, 0x61, 0xde, 0xb6, 0x49, 0xf6, 0xbc, 0x3f, 0x4c, 0xef, 0x38, 0xc4,
        0xf3, 0x55, 0x04, 0xe5, 0x1e, 0xc1 ,0x12, 0xde, 0x5c, 0x38, 0x4d, 0xf7, 0xba, 0x0b, 0x8d, 0x57,
        0x8a, 0x4c, 0x70, 0x2b, 0x6b, 0xf1, 0x1d, 0x5f, 0xac, 0x00, 0x00 ,0x00 ,0x00 };
    }

    public override void Reset()
    {
      base.Reset();
      DatabaseAccounts.ClearCache();
      //TrailHashesAnchor.Clear();
      //IndexTrail = 0;
    }

    public override bool TryGetDB(
      byte[] hash,
      out byte[] dataDB)
    {
      return DatabaseAccounts.TryGetDB(hash, out dataDB);
    }

    public override HeaderDownload CreateHeaderDownload()
    {
      return new HeaderDownloadBToken(
        GetLocator(),
        WinningBlockInHeightAnchorBlock);
    }

    public override List<string> GetSeedAddresses()
    {
      return new List<string>()
      {
        "83.229.86.158"
      };
    }


    const int COUNT_BLOCKS_DOWNLOAD_DEPTH_MAX = 1000;

    public override bool FlagDownloadDBWhenSync(HeaderDownload h)
    {
      return
        h.HeaderTip != null
        &&
        (DatabaseAccounts.GetCountBytes() <
        h.HeaderTip.CountBytesBlocksAccumulated - h.HeaderRoot.CountBytesBlocksAccumulated
        ||
        COUNT_BLOCKS_DOWNLOAD_DEPTH_MAX <
        h.HeaderTip.Height - h.HeaderRoot.Height);
    }
  }
}
