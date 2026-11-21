using System.Net;
using System.Text.Json;

namespace BTokenCore;

public partial class Node
{
  class RPCServer
  {
    Node Node;
    HttpListener HttpListener = new();


    public RPCServer(Node node, int port)
    {
      Node = node;
      HttpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public async Task Start()
    {
      HttpListener.Start();

      while (true)
      {
        HttpListenerContext context = await HttpListener.GetContextAsync().ConfigureAwait(false);

        if (context.Request.HttpMethod == "POST")
          Serve(context);
        else
        {
          context.Response.StatusCode = 405;
          context.Response.Close();
        }
      }
    }

    async Task Serve(HttpListenerContext context)
    {
      try
      {
        using MemoryStream request = new();
        await context.Request.InputStream.CopyToAsync(request).ConfigureAwait(false);

        byte[] response = await Respond(request.ToArray()).ConfigureAwait(false);

        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = response.Length;
        await context.Response.OutputStream.WriteAsync(response).ConfigureAwait(false);
        context.Response.Close();
      }
      catch (HttpListenerException)
      { }
    }

    async Task<byte[]> Respond(byte[] message)
    {
      Dictionary<string, object> response = new() { ["jsonrpc"] = "2.0", ["id"] = null };

      try
      {
        using JsonDocument request = JsonDocument.Parse(message);

        JsonElement root = request.RootElement;

        if (root.TryGetProperty("id", out JsonElement id))
          response["id"] = id.Clone();

        root.TryGetProperty("params", out JsonElement parameters);

        string method = root.GetProperty("method").GetString();

        object result = null;

        switch (method)
        {
          case "startMining":
            await StartMining(parameters.GetProperty("budget").GetInt64()).ConfigureAwait(false);
            break;

          case "stopMining":
            await StopMining().ConfigureAwait(false);
            break;

          case "createTXBTokenUnsigned":
            result = await CreateTXBTokenUnsigned(
              Convert.FromHexString(parameters.GetProperty("keyPublic").GetString()),
              parameters.GetProperty("fee").GetInt64(),
              parameters.GetProperty("outputs").EnumerateArray()
                .Select(o => (o.GetProperty("address").GetString(), o.GetProperty("value").GetInt64()))
                .ToList()).ConfigureAwait(false);
            break;

          case "submitTXBTokenSigned":
            await SubmitTXBTokenSigned(
              Convert.FromHexString(parameters.GetProperty("tXUnsigned").GetString()),
              Convert.FromHexString(parameters.GetProperty("signature").GetString())).ConfigureAwait(false);
            break;

          case "getKeysMiner":
            result = await GetKeysMiner().ConfigureAwait(false);
            break;

          case "withdrawRevenueMiner":
            result = await WithdrawRevenueMiner(
              parameters.GetProperty("address").GetString(),
              parameters.GetProperty("fee").GetInt64()).ConfigureAwait(false);
            break;

          case "withdrawBitcoin":
            result = await WithdrawBitcoin(
              parameters.GetProperty("address").GetString(),
              parameters.GetProperty("value").GetInt64(),
              parameters.GetProperty("fee").GetInt64()).ConfigureAwait(false);
            break;

          case "importKeysMiner":
            await ImportKeysMiner(
              parameters.GetProperty("bitcoin").EnumerateArray().Select(k => k.GetString()).ToList(),
              parameters.GetProperty("bToken").EnumerateArray().Select(k => k.GetString()).ToList()).ConfigureAwait(false);
            break;

          case "getAccount":
            result = await GetAccount(parameters.GetProperty("address").GetString()).ConfigureAwait(false);
            break;

          case "getTXsAccount":
            result = await GetTXsAccount(
              parameters.GetProperty("address").GetString(),
              parameters.GetProperty("height").GetInt32()).ConfigureAwait(false);
            break;

          case "getBalanceAccount":
            result = await GetBalanceAccount(parameters.GetProperty("address").GetString()).ConfigureAwait(false);
            break;

          case "getSnapshotState":
            result = await GetSnapshotState(parameters.GetProperty("countBlocks").GetInt32()).ConfigureAwait(false);
            break;

          default:
            throw new ProtocolException($"Method {method} not found.");
        }

        response["result"] = result;
      }
      catch (Exception exception)
      {
        response["error"] = new Dictionary<string, object> { ["code"] = -32000, ["message"] = exception.Message };
      }

      return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    async Task StartMining(long budget)
    {
      await Node.BlockchainBToken.RunLocked(() => Node.MinerBToken.StartMining(budget));
    }

    async Task StopMining()
    {
      await Node.BlockchainBToken.RunLocked(Node.MinerBToken.StopMining);
    }

    async Task<Dictionary<string, object>> GetSnapshotState(int countBlocks)
    {
      return new Dictionary<string, object>
      {
        ["networks"] = new Dictionary<string, object>
        {
          ["Bitcoin"] = Node.NetworkBitcoin.GetSnapshotState(),
          ["BToken"] = Node.NetworkBToken.GetSnapshotState()
        },
        ["blockchains"] = await Node.BlockchainBToken.RunLocked(() =>
        {
          Dictionary<string, object> snapshotBToken = Node.BlockchainBToken.GetSnapshotState(countBlocks);
          snapshotBToken["miner"] = Node.MinerBToken.GetSnapshotState();

          return new Dictionary<string, object>
          {
            ["Bitcoin"] = Node.BlockchainBitcoin.GetSnapshotState(countBlocks),
            ["BToken"] = snapshotBToken
          };
        })
      };
    }

    async Task<Dictionary<string, object>> CreateTXBTokenUnsigned(byte[] keyPublic, long fee, List<(string address, long value)> outputs)
    {
      List<TXOutputP2PKH> tXOutputs = outputs.Select(o => new TXOutputP2PKH(o.value, o.address.Base58CheckToBinary())).ToList();

      BlockchainBToken.TXBToken tX = await Node.BlockchainBToken.RunLocked(() => Node.BlockchainBToken.CreateTXBTokenUnsigned(keyPublic, fee, tXOutputs));

      return new Dictionary<string, object>
      {
        ["tXUnsigned"] = Convert.ToHexString(tX.TXRaw),
        ["dataToBeSigned"] = Convert.ToHexString([.. Node.BlockchainBToken.IDToken, .. tX.TXRaw]),
        ["hash"] = Convert.ToHexString(tX.Hash),
        ["nonce"] = tX.Nonce,
        ["heightAccountCreated"] = tX.HeightAccountCreated
      };
    }

    async Task<Dictionary<string, object>> GetKeysMiner()
    {
      return await Node.BlockchainBToken.RunLocked(() => new Dictionary<string, object>
      {
        ["bitcoin"] = Node.AnchorerBitcoin.Wallet.GetKeysPrivate(),
        ["bToken"] = Node.MinerBToken.Wallet.GetKeysPrivate()
      });
    }

    async Task<Dictionary<string, object>> WithdrawRevenueMiner(string address, long fee)
    {
      byte[] iDAccount = address.Base58CheckToBinary();

      return await Node.BlockchainBToken.RunLocked(() =>
      {
        List<BlockchainBToken.TXBToken> tXs = Node.MinerBToken.CreateTXsRevenue(iDAccount, fee);

        tXs.ForEach(t => Node.NetworkBToken.SendTX(t.TXRaw));

        return new Dictionary<string, object>
        {
          ["countPayments"] = tXs.Count,
          ["value"] = tXs.Sum(t => t.TXOutputs.OfType<TXOutputP2PKH>().Sum(o => o.Value))
        };
      });
    }

    async Task<Dictionary<string, object>> WithdrawBitcoin(string address, long value, long fee)
    {
      byte[] iDAccount = address.Base58CheckToBinary();

      BlockchainBitcoin.TXBitcoin tX = await Node.BlockchainBToken.RunLocked(() => Node.AnchorerBitcoin.CreateTXWithdrawal(iDAccount, value, fee));

      return new Dictionary<string, object>
      {
        ["hash"] = tX.Hash.ToHexString(),
        ["value"] = value,
        ["fee"] = tX.Fee
      };
    }

    async Task ImportKeysMiner(List<string> keysBitcoin, List<string> keysBToken)
    {
      await Node.BlockchainBToken.RunLocked(() =>
      {
        Node.AnchorerBitcoin.Wallet.AddKeys(keysBitcoin);
        Node.MinerBToken.Wallet.AddKeys(keysBToken);
      });
    }

    async Task<Dictionary<string, object>> GetAccount(string address)
    {
      byte[] iDAccount = address.Base58CheckToBinary();

      if (await Node.BlockchainBToken.RunLocked(() => Node.BlockchainBToken.GetAccount(iDAccount)) is not BlockchainBToken.Account account)
        return null;

      return new Dictionary<string, object>
      {
        ["heightAccountCreated"] = account.HeightAccountCreated,
        ["nonce"] = account.Nonce
      };
    }

    async Task<Dictionary<string, object>> GetTXsAccount(string address, int height)
    {
      byte[] iDAccount = address.Base58CheckToBinary();

      if (await Node.BlockchainBToken.RunLocked(() => Node.BlockchainBToken.GetBlockTXsAccount(iDAccount, height)) is not Block block)
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

    async Task<long?> GetBalanceAccount(string address)
    {
      byte[] iDAccount = address.Base58CheckToBinary();

      return await Node.BlockchainBToken.RunLocked(() => Node.BlockchainBToken.GetBalanceAccount(iDAccount));
    }

    async Task SubmitTXBTokenSigned(byte[] tXUnsigned, byte[] signature)
    {
      byte[] tXRaw = [.. tXUnsigned, (byte)signature.Length, .. signature];

      await Node.BlockchainBToken.RunLocked(() => Node.NetworkBToken.SendTX(tXRaw));
    }
  }
}
