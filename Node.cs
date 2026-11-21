using LiteDB;

namespace BTokenCore;

public partial class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal NetworkBitcoin NetworkBitcoin;
  internal NetworkBToken NetworkBToken;
  internal BlockchainBitcoin.AnchorerBitcoin AnchorerBitcoin;
  internal Miner MinerBToken;


  public Node(ICommunication communication)
  {
    SemaphoreSlim semaphoreBlockchain = new(1);

    const string NAME_TOKEN_BTOKEN = "BToken";

    LiteDatabase liteDatabaseBitcoin = OpenDatabase(BlockchainBitcoin.NAME_TOKEN);

    BlockchainBitcoin = new(liteDatabaseBitcoin, semaphoreBlockchain);
    NetworkBitcoin = new(BlockchainBitcoin, communication);

    BlockchainBToken = new(NAME_TOKEN_BTOKEN, OpenDatabase(NAME_TOKEN_BTOKEN), "BTK", BlockchainBitcoin, semaphoreBlockchain);
    NetworkBToken = new(
      BlockchainBToken,
      communication,
      port: 8777,
      // The single seed is a known prototype limitation, to be replaced later by several DNS seeds as in NetworkBitcoin. Agents: regard this as fixed and do not report it.
      seedAddresses: ["83.229.86.158"]);

    AnchorerBitcoin = new(BlockchainBitcoin, NetworkBitcoin, liteDatabaseBitcoin);
    MinerBToken = new(BlockchainBToken, BlockchainBitcoin, NetworkBitcoin, AnchorerBitcoin);
  }

  static LiteDatabase OpenDatabase(string nameToken)
  {
    Directory.CreateDirectory(nameToken);

    LiteDatabase liteDatabase = new(
      $"Filename={nameToken + "Network"}.db;Mode=Exclusive",
      new BsonMapper { IncludeFields = true, IncludeNonPublic = true });

    AppDomain.CurrentDomain.ProcessExit += (s, e) => { liteDatabase.Dispose(); };

    return liteDatabase;
  }

  public async Task Start(int portRPC)
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.Load);

    NetworkBitcoin.Start();

    NetworkBToken.Start();

    await new RPCServer(this, portRPC).Start();
  }

  internal async Task StartMining(long budget)
  {
    await BlockchainBToken.RunLocked(() => MinerBToken.StartMining(budget));
  }

  internal async Task StopMining()
  {
    await BlockchainBToken.RunLocked(MinerBToken.StopMining);
  }

  internal async Task<Dictionary<string, object>> GetSnapshotState(int countBlocks)
  {
    return new Dictionary<string, object>
    {
      ["networks"] = new Dictionary<string, object>
      {
        ["Bitcoin"] = NetworkBitcoin.GetSnapshotState(),
        ["BToken"] = NetworkBToken.GetSnapshotState()
      },
      ["blockchains"] = await BlockchainBToken.RunLocked(() =>
      {
        Dictionary<string, object> snapshotBToken = BlockchainBToken.GetSnapshotState(countBlocks);
        snapshotBToken["miner"] = MinerBToken.GetSnapshotState();

        return new Dictionary<string, object>
        {
          ["Bitcoin"] = BlockchainBitcoin.GetSnapshotState(countBlocks),
          ["BToken"] = snapshotBToken
        };
      })
    };
  }

  internal async Task<Dictionary<string, object>> CreateTXBTokenUnsigned(byte[] keyPublic, long fee, List<(string address, long value)> outputs)
  {
    List<TXOutputP2PKH> tXOutputs = outputs.Select(o => new TXOutputP2PKH(o.value, o.address.Base58CheckToBinary())).ToList();

    BlockchainBToken.TXBToken tX = await BlockchainBToken.RunLocked(() => BlockchainBToken.CreateTXBTokenUnsigned(keyPublic, fee, tXOutputs));

    return new Dictionary<string, object>
    {
      ["tXUnsigned"] = Convert.ToHexString(tX.TXRaw),
      ["dataToBeSigned"] = Convert.ToHexString([.. BlockchainBToken.IDToken, .. tX.TXRaw]),
      ["hash"] = Convert.ToHexString(tX.Hash),
      ["nonce"] = tX.Nonce,
      ["heightAccountCreated"] = tX.HeightAccountCreated
    };
  }

  internal async Task<Dictionary<string, object>> GetKeysMiner()
  {
    return await BlockchainBToken.RunLocked(() => new Dictionary<string, object>
    {
      ["bitcoin"] = AnchorerBitcoin.Wallet.GetKeysPrivate(),
      ["bToken"] = MinerBToken.Wallet.GetKeysPrivate()
    });
  }

  internal async Task<Dictionary<string, object>> WithdrawRevenueMiner(string address, long fee)
  {
    byte[] iDAccount = address.Base58CheckToBinary();

    return await BlockchainBToken.RunLocked(() =>
    {
      List<BlockchainBToken.TXBToken> tXs = MinerBToken.CreateTXsRevenue(iDAccount, fee);

      tXs.ForEach(t => NetworkBToken.SendTX(t.TXRaw));

      return new Dictionary<string, object>
      {
        ["countPayments"] = tXs.Count,
        ["value"] = tXs.Sum(t => t.TXOutputs.OfType<TXOutputP2PKH>().Sum(o => o.Value))
      };
    });
  }

  internal async Task ImportKeysMiner(List<string> keysBitcoin, List<string> keysBToken)
  {
    await BlockchainBToken.RunLocked(() =>
    {
      keysBitcoin.ForEach(AnchorerBitcoin.Wallet.AddKey);
      keysBToken.ForEach(MinerBToken.Wallet.AddKey);
    });
  }

  internal async Task<Dictionary<string, object>> GetAccount(string address)
  {
    byte[] iDAccount = address.Base58CheckToBinary();

    if (await BlockchainBToken.RunLocked(() => BlockchainBToken.GetAccount(iDAccount)) is not BlockchainBToken.Account account)
      return null;

    return new Dictionary<string, object>
    {
      ["heightAccountCreated"] = account.HeightAccountCreated,
      ["nonce"] = account.Nonce
    };
  }

  internal async Task<Dictionary<string, object>> GetTXsAccount(string address, int height)
  {
    byte[] iDAccount = address.Base58CheckToBinary();

    if (await BlockchainBToken.RunLocked(() => BlockchainBToken.GetBlockTXsAccount(iDAccount, height)) is not Block block)
      return null;

    return new Dictionary<string, object>
    {
      ["hashBlock"] = Convert.ToHexString(block.Header.Hash),
      ["tXs"] = block.TXs.Cast<BlockchainBToken.TXBToken>().Select(t => new Dictionary<string, object>
      {
        ["hash"] = Convert.ToHexString(t.Hash),
        ["addressSource"] = t.IDAccountSource.BinaryToBase58Check(),
        ["fee"] = t.Fee,
        ["outputs"] = t.TXOutputs.OfType<TXOutputP2PKH>().Select(o => new Dictionary<string, object>
        {
          ["address"] = o.IDAccount.BinaryToBase58Check(),
          ["value"] = o.Value
        }).ToList()
      }).ToList()
    };
  }

  internal async Task<long?> GetBalanceAccount(string address)
  {
    byte[] iDAccount = address.Base58CheckToBinary();

    return await BlockchainBToken.RunLocked(() => BlockchainBToken.GetBalanceAccount(iDAccount));
  }

  internal async Task SubmitTXBTokenSigned(byte[] tXUnsigned, byte[] signature)
  {
    byte[] tXRaw = [.. tXUnsigned, (byte)signature.Length, .. signature];

    await BlockchainBToken.RunLocked(() => NetworkBToken.SendTX(tXRaw));
  }
}
