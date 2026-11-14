using System.Net;
using System.Text.Json;

namespace BTokenCore;

internal class RPCServer
{
  Node Node;
  HttpListener HttpListener = new();


  internal RPCServer(Node node, int port)
  {
    Node = node;
    HttpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
  }

  internal async Task Start()
  {
    HttpListener.Start();

    while (true)
    {
      HttpListenerContext context = await HttpListener.GetContextAsync().ConfigureAwait(false);

      try
      {
        await Respond(context).ConfigureAwait(false);
      }
      catch (HttpListenerException)
      { }
    }
  }

  async Task Respond(HttpListenerContext context)
  {
    Dictionary<string, object> response = new() { ["jsonrpc"] = "2.0", ["id"] = null };

    try
    {
      using JsonDocument request = await JsonDocument.ParseAsync(context.Request.InputStream).ConfigureAwait(false);

      JsonElement root = request.RootElement;

      if (root.TryGetProperty("id", out JsonElement id))
        response["id"] = id.Clone();

      root.TryGetProperty("params", out JsonElement parameters);

      string method = root.GetProperty("method").GetString();

      switch (method)
      {
        case "startMining":
          await Node.StartMining(
            parameters.GetProperty("countAnchorsPer10Minutes").GetInt32(),
            parameters.GetProperty("feeAnchor").GetInt64()).ConfigureAwait(false);
          break;

        case "stopMining":
          await Node.StopMining().ConfigureAwait(false);
          break;

        case "submitTXBToken":
          await Node.SubmitTXBToken(
            Convert.FromHexString(parameters.GetProperty("tXRaw").GetString())).ConfigureAwait(false);
          break;

        default:
          throw new ProtocolException($"Method {method} not found.");
      }

      response["result"] = null;
    }
    catch (Exception exception)
    {
      response["error"] = new Dictionary<string, object> { ["code"] = -32000, ["message"] = exception.Message };
    }

    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response);

    context.Response.ContentType = "application/json";
    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    context.Response.Close();
  }
}
