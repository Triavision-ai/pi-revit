using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RevitBridge
{
    /// <summary>
    /// Minimal localhost HTTP bridge. Routes:
    ///
    ///   GET  /ping                  -> bridge availability
    ///   GET  /tools                 -> tool metadata list
    ///   GET  /tools/{name}          -> one tool's metadata
    ///   POST /tools/{name}/execute  -> execute a tool on the Revit API thread
    ///
    /// Every request must carry ?token=... matching the per-start token written
    /// to %APPDATA%\RevitBridge\bridge.json. The HTTP handling is deliberately
    /// minimal (loopback only) with byte-accurate UTF-8 body reads.
    /// </summary>
    internal sealed class BridgeServer : IDisposable
    {
        private const int DefaultPort = 47777;
        private const int MaxPort = 47797;
        private const int MaxRequestBytes = 4_000_000;
        private const int MaxContentChars = 12_000;

        private readonly CommandQueue _queue;
        private readonly ToolRegistry _registry;
        private readonly string _revitVersion;
        private readonly Func<bool> _hasOpenDocument;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private string _token = string.Empty;
        private int _port;

        public BridgeServer(CommandQueue queue, ToolRegistry registry, string revitVersion, Func<bool> hasOpenDocument)
        {
            _queue = queue;
            _registry = registry;
            _revitVersion = revitVersion;
            _hasOpenDocument = hasOpenDocument;
        }

        public void Start()
        {
            if (_listener != null) return;

            _token = Guid.NewGuid().ToString("N");
            _cts = new CancellationTokenSource();

            for (int port = ResolveStartPort(); port <= MaxPort; port++)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, port);
                    _listener.Start();
                    _port = port;
                    break;
                }
                catch
                {
                    _listener = null;
                }
            }

            if (_listener is null)
                throw new InvalidOperationException($"Unable to start the Revit bridge on localhost ports {DefaultPort}-{MaxPort}.");

            WriteBridgeInfo();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            DeleteBridgeInfoIfOwned();
        }

        public void Dispose() => Stop();

        private static int ResolveStartPort()
        {
            var raw = Environment.GetEnvironmentVariable("REVIT_BRIDGE_PORT");
            return int.TryParse(raw, out int port) && port > 0 ? port : DefaultPort;
        }

        internal static string BridgeInfoDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitBridge");

        public static string BridgeInfoPath() => Path.Combine(BridgeInfoDirectory(), "bridge.json");

        private void WriteBridgeInfo()
        {
            Directory.CreateDirectory(BridgeInfoDirectory());
            File.WriteAllText(BridgeInfoPath(), JsonSerializer.Serialize(new
            {
                baseUrl = $"http://127.0.0.1:{_port}",
                token = _token,
                pid = Environment.ProcessId,
                revitVersion = _revitVersion,
                startedAtUtc = DateTime.UtcNow.ToString("o"),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void DeleteBridgeInfoIfOwned()
        {
            try
            {
                string path = BridgeInfoPath();
                if (!File.Exists(path)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out int value) && value == Environment.ProcessId)
                    File.Delete(path);
            }
            catch
            {
                // Never let cleanup break Revit shutdown.
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
                catch (OperationCanceledException)
                {
                    client?.Dispose();
                    break;
                }
                catch
                {
                    client?.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using var _ = client;
            NetworkStream? stream = null;
            try
            {
                stream = client.GetStream();
                var request = await ReadRequestAsync(stream, token);
                if (request is null) return;

                var (method, target, body) = request.Value;
                var uri = new Uri("http://127.0.0.1" + target);
                var query = ParseQuery(uri.Query);

                if (!query.TryGetValue("token", out var tokenValue) || !string.Equals(tokenValue, _token, StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 403, new { error = true, message = "Invalid Revit bridge token." }, token);
                    return;
                }

                var (status, response) = await RouteAsync(method, uri.AbsolutePath, query, body, token);
                await WriteJsonAsync(stream, status, response, token);
            }
            catch (Exception ex)
            {
                if (stream != null)
                {
                    try { await WriteJsonAsync(stream, 500, new { error = true, message = ex.Message }, token); }
                    catch { }
                }
            }
        }

        /// <summary>Reads one HTTP request with a byte-accurate Content-Length body.</summary>
        private static async Task<(string Method, string Target, string Body)?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[8192];
            using var data = new MemoryStream();
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(buffer, token);
                if (n <= 0) return null;
                data.Write(buffer, 0, n);
                if (data.Length > MaxRequestBytes)
                    throw new InvalidOperationException("Request too large.");
                headerEnd = FindHeaderEnd(data.GetBuffer(), (int)data.Length);
            }

            string headerText = Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd);
            string[] lines = headerText.Split("\r\n");
            string[] requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length < 2)
                throw new InvalidOperationException("Invalid HTTP request line.");

            string method = requestParts[0].ToUpperInvariant();
            if (method != "GET" && method != "POST")
                throw new InvalidOperationException("Only GET and POST are supported.");

            int contentLength = 0;
            foreach (string line in lines.Skip(1))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0) continue;
                if (line.AsSpan(0, separator).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.AsSpan(separator + 1).Trim(), out contentLength);
            }

            if (contentLength is < 0 or > MaxRequestBytes)
                throw new InvalidOperationException("Invalid Content-Length.");

            int bodyStart = headerEnd + 4;
            var bodyBytes = new byte[contentLength];
            int alreadyRead = Math.Min((int)data.Length - bodyStart, contentLength);
            Array.Copy(data.GetBuffer(), bodyStart, bodyBytes, 0, alreadyRead);

            int read = alreadyRead;
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(read, contentLength - read), token);
                if (n <= 0) break;
                read += n;
            }

            return (method, requestParts[1], Encoding.UTF8.GetString(bodyBytes, 0, read));
        }

        private static int FindHeaderEnd(byte[] data, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                    return i - 3;
            }
            return -1;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                result[WebUtility.UrlDecode(pair[..separator])] = WebUtility.UrlDecode(pair[(separator + 1)..]);
            }
            return result;
        }

        private async Task<(int Status, object Response)> RouteAsync(string method, string path, Dictionary<string, string> query, string body, CancellationToken token)
        {
            if (method == "GET" && path == "/ping")
                return (200, new { ok = true, service = "revit-bridge", revitVersion = _revitVersion, pid = Environment.ProcessId });

            if (method == "GET" && path == "/tools")
            {
                var tools = _registry.DescribeAll();
                return (200, new { protocolVersion = 1, tools, count = tools.Count });
            }

            const string toolPrefix = "/tools/";
            if (path.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string tail = path[toolPrefix.Length..].Trim('/');

                if (tail.EndsWith("/execute", StringComparison.OrdinalIgnoreCase))
                {
                    if (method != "POST")
                        return (405, new { error = true, message = "Tool execution requires POST." });

                    string name = WebUtility.UrlDecode(tail[..^"/execute".Length].Trim('/'));
                    return await ExecuteToolAsync(name, body, query, token);
                }

                if (method != "GET")
                    return (405, new { error = true, message = "Tool description requires GET." });

                string describeName = WebUtility.UrlDecode(tail);
                var tool = _registry.Get(describeName);
                return tool is null
                    ? (404, new { error = true, message = $"Unknown tool: {describeName}" })
                    : (200, _registry.Describe(tool));
            }

            return (404, new { error = true, message = $"Unknown Revit bridge endpoint: {path}" });
        }

        private async Task<(int Status, object Response)> ExecuteToolAsync(string name, string body, Dictionary<string, string> query, CancellationToken token)
        {
            var tool = _registry.Get(name);
            if (tool is null)
                return (404, new { error = true, message = $"Unknown tool: {name}" });

            // Pre-check before enqueueing: with zero documents open Revit does not
            // pump ExternalEvents, so a queued call would hang instead of failing.
            // The in-queue NoActiveDocumentException below remains as the backstop
            // for a document closing between this check and execution. Tools that
            // never touch the Revit API (RequiresDocument = false) skip the gate.
            if (tool.RequiresDocument && !_hasOpenDocument())
                return (409, new { error = true, hasActiveDocument = false, message = "No active Revit document is open." });

            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return (400, new { error = true, message = "Tool arguments must be a JSON object." });
                args = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return (400, new { error = true, message = $"Invalid JSON tool arguments: {ex.Message}" });
            }

            int timeoutMs = ResolveTimeoutMs(query);

            try
            {
                object? output = tool.RequiresDocument
                    ? await _queue.RunAsync(uiApp =>
                    {
                        var document = uiApp.ActiveUIDocument?.Document ?? throw new NoActiveDocumentException();
                        return tool.Execute(args, new ToolContext(document, uiApp));
                    }, TimeSpan.FromMilliseconds(timeoutMs))
                    // RequiresDocument = false tools never touch the Revit API, so they
                    // run right here on the server task instead of the CommandQueue.
                    : tool.Execute(args, new ToolContext(null, null));

                return (200, BuildToolResponse(name, output));
            }
            catch (NoActiveDocumentException ex)
            {
                return (409, new { error = true, hasActiveDocument = false, message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return (400, new { error = true, toolName = name, message = ex.Message });
            }
            catch (Exception ex)
            {
                return (500, new { error = true, toolName = name, message = ex.Message });
            }
        }

        /// <summary>Optional timeout_ms query param, clamped to 1s..10min; default 30s.</summary>
        private static int ResolveTimeoutMs(Dictionary<string, string> query)
            => query.TryGetValue("timeout_ms", out var raw) && int.TryParse(raw, out int value)
                ? Math.Clamp(value, 1_000, 600_000)
                : 30_000;

        /// <summary>Compact text for model context; full payload in details. Tools may
        /// return a ToolOutput (CompactText -> content, Payload -> details.payload) or
        /// any plain object (serialized to both).</summary>
        private static object BuildToolResponse(string toolName, object? output)
        {
            object? payload = output;
            string? compact = null;
            if (output is ToolOutput toolOutput)
            {
                payload = toolOutput.Payload;
                compact = toolOutput.CompactText;
            }

            string text = compact ?? JsonSerializer.Serialize(payload ?? new { });
            bool truncated = text.Length > MaxContentChars;
            if (truncated)
            {
                string suffix = $"... [truncated at {MaxContentChars} chars; full payload is in details]";
                text = text[..Math.Max(0, MaxContentChars - suffix.Length)] + suffix;
            }

            return new
            {
                success = true,
                toolName,
                content = new[] { new { type = "text", text } },
                details = new { payload, contentTruncated = truncated },
                isError = false,
            };
        }

        private static async Task WriteJsonAsync(NetworkStream stream, int status, object value, CancellationToken token)
        {
            string reason = status switch
            {
                200 => "OK",
                400 => "Bad Request",
                403 => "Forbidden",
                404 => "Not Found",
                405 => "Method Not Allowed",
                409 => "Conflict",
                500 => "Internal Server Error",
                _ => "OK",
            };

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
            string header =
                $"HTTP/1.1 {status} {reason}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
            await stream.WriteAsync(body, token);
            await stream.FlushAsync(token);
        }
    }
}
