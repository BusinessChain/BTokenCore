using System.Net;
using System.Text.Json;

namespace BTokenCore;

public class RPCServer
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
          await Node.StartMining(parameters.GetProperty("budget").GetInt64()).ConfigureAwait(false);
          break;

        case "stopMining":
          await Node.StopMining().ConfigureAwait(false);
          break;

        case "createTXBTokenUnsigned":
          result = await Node.CreateTXBTokenUnsigned(
            Convert.FromHexString(parameters.GetProperty("keyPublic").GetString()),
            parameters.GetProperty("fee").GetInt64(),
            parameters.GetProperty("outputs").EnumerateArray()
              .Select(o => (o.GetProperty("address").GetString(), o.GetProperty("value").GetInt64()))
              .ToList()).ConfigureAwait(false);
          break;

        case "submitTXBTokenSigned":
          await Node.SubmitTXBTokenSigned(
            Convert.FromHexString(parameters.GetProperty("tXUnsigned").GetString()),
            Convert.FromHexString(parameters.GetProperty("signature").GetString())).ConfigureAwait(false);
          break;

        case "getAccount":
          result = await Node.GetAccount(parameters.GetProperty("address").GetString()).ConfigureAwait(false);
          break;

        case "getTXsAccount":
          result = await Node.GetTXsAccount(
            parameters.GetProperty("address").GetString(),
            parameters.GetProperty("height").GetInt32()).ConfigureAwait(false);
          break;

        case "getBalanceAccount":
          result = await Node.GetBalanceAccount(parameters.GetProperty("address").GetString()).ConfigureAwait(false);
          break;

        case "getSnapshotState":
          result = await Node.GetSnapshotState(parameters.GetProperty("countBlocks").GetInt32()).ConfigureAwait(false);
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
}
