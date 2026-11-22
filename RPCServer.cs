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
            await StartMining(ReadInt64(parameters, "budget")).ConfigureAwait(false);
            break;

          case "stopMining":
            await StopMining().ConfigureAwait(false);
            break;

          case "setWithdrawalAutoMiner":
            await SetWithdrawalAutoMiner(
              ReadIDAccount(parameters, "address"),
              ReadInt64(parameters, "fee"),
              ReadInt64(parameters, "level")).ConfigureAwait(false);
            break;

          case "stopWithdrawalAutoMiner":
            await StopWithdrawalAutoMiner().ConfigureAwait(false);
            break;

          case "createTXBTokenUnsigned":
            result = await CreateTXBTokenUnsigned(
              ReadBytesHex(parameters, "keyPublic"),
              ReadInt64(parameters, "fee"),
              ReadArray(parameters, "outputs")
                .Select((o, i) => (ReadIDAccount(o, "address", $"outputs[{i}]."), ReadInt64(o, "value", $"outputs[{i}].")))
                .ToList()).ConfigureAwait(false);
            break;

          case "submitTXBTokenSigned":
            await SubmitTXBTokenSigned(
              ReadBytesHex(parameters, "tXUnsigned"),
              ReadBytesHex(parameters, "signature")).ConfigureAwait(false);
            break;

          case "getKeysMiner":
            result = await GetKeysMiner().ConfigureAwait(false);
            break;

          case "withdrawRevenueMiner":
            result = await WithdrawRevenueMiner(
              ReadIDAccount(parameters, "address"),
              ReadInt64(parameters, "fee")).ConfigureAwait(false);
            break;

          case "withdrawBitcoin":
            result = await WithdrawBitcoin(
              ReadIDAccount(parameters, "address"),
              ReadInt64(parameters, "fee")).ConfigureAwait(false);
            break;

          case "importKeysMiner":
            await ImportKeysMiner(
              ReadStrings(parameters, "bitcoin"),
              ReadStrings(parameters, "bToken")).ConfigureAwait(false);
            break;

          case "getAccount":
            result = await GetAccount(ReadIDAccount(parameters, "address")).ConfigureAwait(false);
            break;

          case "getTXsAccount":
            result = await GetTXsAccount(
              ReadIDAccount(parameters, "address"),
              ReadInt32(parameters, "height")).ConfigureAwait(false);
            break;

          case "getBalanceAccount":
            result = await GetBalanceAccount(ReadIDAccount(parameters, "address")).ConfigureAwait(false);
            break;

          case "getSnapshotState":
            result = await GetSnapshotState(ReadInt32(parameters, "countBlocks")).ConfigureAwait(false);
            break;

          default:
            throw new ProtocolException($"Method {method} not found.");
        }

        response["result"] = result;
      }
      catch (Exception exception)
      {
        Dictionary<string, object> error = new() { ["code"] = -32000, ["message"] = exception.Message };

        if (exception is ProtocolException exceptionProtocol && exceptionProtocol.Parameters.Count > 0)
          error["data"] = new Dictionary<string, object> { ["parameters"] = exceptionProtocol.Parameters };

        response["error"] = error;
      }

      return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    static JsonElement GetParameter(JsonElement element, string property, string name)
    {
      if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement parameter))
        throw new ProtocolException($"The parameter {name} is missing.", name);

      if (parameter.ValueKind == JsonValueKind.Null)
        throw new ProtocolException($"The parameter {name} is empty.", name);

      return parameter;
    }

    static long ReadInt64(JsonElement element, string property, string prefix = "")
    {
      string name = prefix + property;
      JsonElement parameter = GetParameter(element, property, name);

      if (parameter.ValueKind != JsonValueKind.Number || !parameter.TryGetInt64(out long value))
        throw new ProtocolException($"The parameter {name} must be an integer.", name);

      return value;
    }

    static int ReadInt32(JsonElement element, string property)
    {
      long value = ReadInt64(element, property);

      if (value < int.MinValue || value > int.MaxValue)
        throw new ProtocolException($"The parameter {property} is out of range.", property);

      return (int)value;
    }

    static string ReadString(JsonElement element, string property, string prefix = "")
    {
      string name = prefix + property;
      JsonElement parameter = GetParameter(element, property, name);

      if (parameter.ValueKind != JsonValueKind.String)
        throw new ProtocolException($"The parameter {name} must be a text.", name);

      string value = parameter.GetString();

      if (value.Length == 0)
        throw new ProtocolException($"The parameter {name} is empty.", name);

      return value;
    }

    static List<JsonElement> ReadArray(JsonElement element, string property)
    {
      JsonElement parameter = GetParameter(element, property, property);

      if (parameter.ValueKind != JsonValueKind.Array)
        throw new ProtocolException($"The parameter {property} must be a list.", property);

      return parameter.EnumerateArray().ToList();
    }

    static List<string> ReadStrings(JsonElement element, string property)
    {
      List<JsonElement> entries = ReadArray(element, property);

      if (entries.Any(k => k.ValueKind != JsonValueKind.String || k.GetString().Length == 0))
        throw new ProtocolException($"The parameter {property} contains an entry that is empty or not a text.", property);

      return entries.Select(k => k.GetString()).ToList();
    }

    static byte[] ReadBytesHex(JsonElement element, string property)
    {
      string hex = ReadString(element, property);

      try
      {
        return Convert.FromHexString(hex);
      }
      catch (FormatException)
      {
        throw new ProtocolException($"The parameter {property} is not hexadecimal.", property);
      }
    }

    static byte[] ReadIDAccount(JsonElement element, string property, string prefix = "")
    {
      string address = ReadString(element, property, prefix);

      try
      {
        return address.Base58CheckToBinary();
      }
      catch (FormatException exception)
      {
        throw new ProtocolException(exception.Message, prefix + property);
      }
    }

    async Task StartMining(long budget)
    {
      await Node.BlockchainBToken.RunLocked(() => Node.MinerBToken.StartMining(budget));
    }

    async Task StopMining()
    {
      await Node.BlockchainBToken.RunLocked(Node.MinerBToken.StopMining);
    }

    async Task SetWithdrawalAutoMiner(byte[] iDAccount, long fee, long level)
    {
      await Node.BlockchainBToken.RunLocked(() => Node.MinerBToken.SetWithdrawalAuto(iDAccount, fee, level));
    }

    async Task StopWithdrawalAutoMiner()
    {
      await Node.BlockchainBToken.RunLocked(Node.MinerBToken.StopWithdrawalAuto);
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
          Dictionary<string, object> snapshotBitcoin = Node.BlockchainBitcoin.GetSnapshotState(countBlocks);
          snapshotBitcoin["anchorer"] = Node.AnchorerBitcoin.GetSnapshotState();

          Dictionary<string, object> snapshotBToken = Node.BlockchainBToken.GetSnapshotState(countBlocks);
          snapshotBToken["miner"] = Node.MinerBToken.GetSnapshotState();

          return new Dictionary<string, object>
          {
            ["Bitcoin"] = snapshotBitcoin,
            ["BToken"] = snapshotBToken
          };
        })
      };
    }

    async Task<Dictionary<string, object>> CreateTXBTokenUnsigned(byte[] keyPublic, long fee, List<(byte[] iDAccount, long value)> outputs)
    {
      List<TXOutputP2PKH> tXOutputs = outputs.Select(o => new TXOutputP2PKH(o.value, o.iDAccount)).ToList();

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

    async Task<Dictionary<string, object>> WithdrawRevenueMiner(byte[] iDAccount, long fee)
    {
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

    async Task<Dictionary<string, object>> WithdrawBitcoin(byte[] iDAccount, long fee)
    {
      BlockchainBitcoin.TXBitcoin tX = await Node.BlockchainBToken.RunLocked(() =>
      {
        Node.MinerBToken.StopMining();
        return Node.AnchorerBitcoin.CreateTXWithdrawal(iDAccount, fee);
      });

      return new Dictionary<string, object>
      {
        ["hash"] = tX.Hash.ToHexString(),
        ["value"] = tX.GetValueOutputs(),
        ["fee"] = tX.Fee
      };
    }

    async Task ImportKeysMiner(List<string> keysBitcoin, List<string> keysBToken)
    {
      await Node.BlockchainBToken.RunLocked(() =>
      {
        foreach ((Wallet wallet, List<string> keys, string name) in new[] { (Node.AnchorerBitcoin.Wallet, keysBitcoin, "bitcoin"), (Node.MinerBToken.Wallet, keysBToken, "bToken") })
          try
          {
            wallet.AddKeys(keys);
          }
          catch (FormatException exception)
          {
            throw new ProtocolException(exception.Message, name);
          }
      });
    }

    async Task<Dictionary<string, object>> GetAccount(byte[] iDAccount)
    {
      if (await Node.BlockchainBToken.RunLocked(() => Node.BlockchainBToken.GetAccount(iDAccount)) is not BlockchainBToken.Account account)
        return null;

      return new Dictionary<string, object>
      {
        ["heightAccountCreated"] = account.HeightAccountCreated,
        ["nonce"] = account.Nonce
      };
    }

    async Task<Dictionary<string, object>> GetTXsAccount(byte[] iDAccount, int height)
    {
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

    async Task<Dictionary<string, object>> GetBalanceAccount(byte[] iDAccount)
    {
      return await Node.BlockchainBToken.RunLocked(() =>
      {
        if (Node.BlockchainBToken.GetBalanceAccount(iDAccount) is not long balance)
          return null;

        return new Dictionary<string, object>
        {
          ["balance"] = balance,
          ["valueIncomingPending"] = Node.BlockchainBToken.GetValueIncomingPending(iDAccount)
        };
      });
    }

    async Task SubmitTXBTokenSigned(byte[] tXUnsigned, byte[] signature)
    {
      byte[] tXRaw = [.. tXUnsigned, (byte)signature.Length, .. signature];

      try
      {
        await Node.BlockchainBToken.RunLocked(() => Node.NetworkBToken.SendTX(tXRaw));
      }
      catch (ProtocolException exception)
      {
        throw new ProtocolException(exception.Message, "tXUnsigned", "signature");
      }
    }
  }
}
