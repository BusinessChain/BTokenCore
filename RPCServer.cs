using System.Net;
using System.Net.WebSockets;
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

      if (context.Request.IsWebSocketRequest)
        Serve(context);
      else
      {
        context.Response.StatusCode = 400;
        context.Response.Close();
      }
    }
  }

  async Task Serve(HttpListenerContext context)
  {
    try
    {
      using WebSocket webSocket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;

      while (await ReceiveMessage(webSocket).ConfigureAwait(false) is byte[] message)
        await webSocket.SendAsync(
          new ArraySegment<byte>(await Respond(message).ConfigureAwait(false)),
          WebSocketMessageType.Text,
          endOfMessage: true,
          CancellationToken.None).ConfigureAwait(false);
    }
    catch (WebSocketException)
    { }
    catch (HttpListenerException)
    { }
  }

  static async Task<byte[]> ReceiveMessage(WebSocket webSocket)
  {
    using MemoryStream message = new();
    byte[] buffer = new byte[4096];
    WebSocketReceiveResult result;

    do
    {
      result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);

      if (result.MessageType == WebSocketMessageType.Close)
      {
        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        return null;
      }

      message.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    return message.ToArray();
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

    return JsonSerializer.SerializeToUtf8Bytes(response);
  }
}
