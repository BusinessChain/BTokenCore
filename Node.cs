using LiteDB;

namespace BTokenCore;

public class Node
{
  internal BlockchainBitcoin BlockchainBitcoin;
  internal BlockchainBToken BlockchainBToken;
  internal NetworkBitcoin NetworkBitcoin;
  internal NetworkBToken NetworkBToken;
  internal Miner Miner;


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

    Miner = new(BlockchainBToken, BlockchainBitcoin, NetworkBitcoin, new BlockchainBitcoin.AnchorerBitcoin(BlockchainBitcoin, NetworkBitcoin, liteDatabaseBitcoin));
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

  public async Task Start()
  {
    await BlockchainBToken.RunLocked(BlockchainBToken.Load);

    NetworkBitcoin.Start();

    NetworkBToken.Start();
  }

  internal async Task StartMining(long budget)
  {
    await BlockchainBToken.RunLocked(() => Miner.StartMining(budget));
  }

  internal async Task StopMining()
  {
    await BlockchainBToken.RunLocked(Miner.StopMining);
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
        snapshotBToken["miner"] = Miner.GetSnapshotState();

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
