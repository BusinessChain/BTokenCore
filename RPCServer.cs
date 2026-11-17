using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace BTokenCore;

public class RPCServer
{
  Node Node;
  HttpListener HttpListener = new();
  ConcurrentDictionary<WebSocket, SemaphoreSlim> Connections = new();


  public RPCServer(Node node, int port)
  {
    Node = node;
    HttpListener.Prefixes.Add($"http://127.0.0.1:{port}/");

    Node.BlockchainBToken.OnBlockInserted += block => Push("blockInserted", block);
    Node.BlockchainBToken.OnBlockRolledBack += block => Push("blockRolledBack", block);
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
    WebSocket webSocket = null;

    try
    {
      webSocket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
      Connections[webSocket] = new SemaphoreSlim(1);

      while (await ReceiveMessage(webSocket).ConfigureAwait(false) is byte[] message)
        await Send(webSocket, await Respond(message).ConfigureAwait(false)).ConfigureAwait(false);
    }
    catch (WebSocketException)
    { }
    catch (HttpListenerException)
    { }
    finally
    {
      if (webSocket != null)
      {
        Connections.TryRemove(webSocket, out _);
        webSocket.Dispose();
      }
    }
  }

  void Push(string method, Block block)
  {
    byte[] notification = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
      ["jsonrpc"] = "2.0",
      ["method"] = method,
      ["params"] = new Dictionary<string, object>
      {
        ["height"] = block.Header.Height,
        ["hash"] = block.Header.Hash.ToHexString()
      }
    });

    foreach (WebSocket webSocket in Connections.Keys)
      Send(webSocket, notification);
  }

  async Task Send(WebSocket webSocket, byte[] message)
  {
    if (!Connections.TryGetValue(webSocket, out SemaphoreSlim semaphoreSend))
      return;

    await semaphoreSend.WaitAsync().ConfigureAwait(false);

    try
    {
      await webSocket.SendAsync(
        new ArraySegment<byte>(message),
        WebSocketMessageType.Text,
        endOfMessage: true,
        CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
    {
      Connections.TryRemove(webSocket, out _);
      webSocket.Abort();
    }
    finally
    {
      semaphoreSend.Release();
    }
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

      object result = null;

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

        case "getNetworks":
          result = new Dictionary<string, object>
          {
            ["Bitcoin"] = Node.NetworkBitcoin.GetSnapshotState(),
            ["BToken"] = Node.NetworkBToken.GetSnapshotState()
          };
          break;

        case "getBlockchains":
          result = new Dictionary<string, object>
          {
            ["Bitcoin"] = Node.BlockchainBitcoin.GetSnapshotState(),
            ["BToken"] = Node.BlockchainBToken.GetSnapshotState()
          };
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
