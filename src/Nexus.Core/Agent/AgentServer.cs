using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core.Agent;

/// <summary>
/// A tiny HTTP endpoint on 127.0.0.1 that receives agent hook events.
/// It only accepts status events, requires the per-install token, and can never
/// send anything back to an agent: every accepted request gets an empty 204,
/// which Claude Code treats as "no decision".
/// </summary>
public sealed class AgentServer : IDisposable
{
    public const string EventsPath = "/v1/events";
    private const int MaxBodyBytes = 256 * 1024;
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public AgentServer(int port, string token)
    {
        Port = port;
        _token = token;
    }

    public int Port { get; private set; }

    /// <summary>Raised on a thread-pool thread for every accepted event body.</summary>
    public event Action<AgentEvent>? EventReceived;

    /// <summary>Starts listening. Returns false if the port is taken.</summary>
    public bool TryStart()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(_cts.Token);
            return true;
        }
        catch (SocketException)
        {
            _listener = null;
            return false;
        }
    }

    public static int PickFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public string EventsUrl => $"http://127.0.0.1:{Port}{EventsPath}";

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var stream = client.GetStream();
            var request = await HttpRequestReader.ReadAsync(stream, MaxHeaderBytes, MaxBodyBytes, timeout.Token);
            if (request is null)
            {
                await WriteStatusAsync(stream, 400, "Bad Request", timeout.Token);
                return;
            }

            if (request.Method == "GET" && request.Path == "/v1/health")
            {
                await WriteStatusAsync(stream, 204, "No Content", timeout.Token);
                return;
            }

            if (request.Method != "POST" || request.Path != EventsPath)
            {
                await WriteStatusAsync(stream, 404, "Not Found", timeout.Token);
                return;
            }

            if (!IsAuthorized(request))
            {
                await WriteStatusAsync(stream, 401, "Unauthorized", timeout.Token);
                return;
            }

            var e = AgentEvent.Parse(Encoding.UTF8.GetString(request.Body));
            await WriteStatusAsync(stream, e is null ? 400 : 204, e is null ? "Bad Request" : "No Content", timeout.Token);

            if (e is not null)
            {
                EventReceived?.Invoke(e);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            // Client went away or was too slow. Nothing to do.
        }
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var presented =
            request.Header("authorization") is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth["Bearer ".Length..].Trim()
                : request.Header("x-nexus-token");

        if (presented is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(_token));
    }

    private static async Task WriteStatusAsync(Stream stream, int code, string reason, CancellationToken ct)
    {
        var response = $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
    }
}

internal sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Minimal HTTP/1.1 request reader: request line, headers, Content-Length or chunked body.</summary>
internal static class HttpRequestReader
{
    public static async Task<HttpRequest?> ReadAsync(Stream stream, int maxHeaderBytes, int maxBodyBytes, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd;

        while (true)
        {
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            if (headerEnd >= 0)
            {
                break;
            }

            if (buffer.Length > maxHeaderBytes)
            {
                return null;
            }

            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        var all = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var path = requestLine[1].Split('?')[0];
        var rest = new MemoryStream();
        rest.Write(all, headerEnd + 4, all.Length - headerEnd - 4);

        if (headers.TryGetValue("expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), ct);
        }

        byte[] body;
        if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedAsync(stream, rest, maxBodyBytes, ct) ?? [];
        }
        else
        {
            var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
            if (length < 0 || length > maxBodyBytes)
            {
                return null;
            }

            while (rest.Length < length)
            {
                var read = await stream.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }

                rest.Write(chunk, 0, read);
            }

            body = rest.ToArray().AsSpan(0, (int)Math.Min(length, rest.Length)).ToArray();
        }

        return new HttpRequest(requestLine[0].ToUpperInvariant(), path, headers, body);
    }

    private static async Task<byte[]?> ReadChunkedAsync(Stream stream, MemoryStream pending, int maxBodyBytes, CancellationToken ct)
    {
        var chunk = new byte[4096];
        var body = new MemoryStream();

        async Task<bool> Fill(int needed)
        {
            while (pending.Length - pending.Position < needed)
            {
                var read = await stream.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    return false;
                }

                var pos = pending.Position;
                pending.Seek(0, SeekOrigin.End);
                pending.Write(chunk, 0, read);
                pending.Position = pos;
            }

            return true;
        }

        async Task<string?> ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (!await Fill(1))
                {
                    return null;
                }

                var b = pending.ReadByte();
                if (b == '\n')
                {
                    return sb.ToString().TrimEnd('\r');
                }

                sb.Append((char)b);
                if (sb.Length > 64)
                {
                    return null;
                }
            }
        }

        pending.Position = 0;
        while (true)
        {
            var sizeLine = await ReadLine();
            if (sizeLine is null)
            {
                return null;
            }

            var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                return body.ToArray();
            }

            if (body.Length + size > maxBodyBytes || !await Fill(size + 2))
            {
                return null;
            }

            var data = new byte[size];
            pending.ReadExactly(data);
            body.Write(data);
            pending.Position += 2; // CRLF after each chunk
        }
    }

    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }
}
