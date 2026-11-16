using System.Globalization;
using System.Text;

namespace RevitBridge
{
    /// <summary>A request the bridge cannot frame safely. It is answered with its own status
    /// before any route or tool runs, so no operation receipt is ever created for it.</summary>
    internal sealed class RequestRejectedException : Exception
    {
        public int Status { get; }
        public RequestRejectedException(int status, string message) : base(message) => Status = status;
    }

    /// <summary>
    /// The bridge's minimal HTTP/1.1 framing: reading one request and naming a status. It has
    /// no Revit dependency and works on any Stream, so it is tested without Revit.
    /// </summary>
    internal static class HttpWire
    {
        internal const int MaxRequestBytes = 4_000_000;

        /// <summary>How long a client may take to send one complete request. The bridge only
        /// serves loopback clients, which send a request in milliseconds; a connection that
        /// stays silent longer is closed so it cannot hold a server task forever.</summary>
        internal static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Reason phrase for every status the bridge sends.</summary>
        internal static string ReasonPhrase(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            408 => "Request Timeout",
            409 => "Conflict",
            411 => "Length Required",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            // Never label an unlisted error "OK": the status class decides.
            >= 200 and < 300 => "OK",
            >= 400 and < 500 => "Client Error",
            >= 500 => "Server Error",
            _ => "Unknown",
        };

        /// <summary>Reads one HTTP request with a byte-accurate Content-Length body. Returns
        /// null when the client closed the connection without sending anything. A client that
        /// does not finish its request within <paramref name="readTimeout"/> is rejected with 408.</summary>
        internal static async Task<(string Method, string Target, string Body)?> ReadRequestAsync(Stream stream, TimeSpan readTimeout, CancellationToken token)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(readTimeout);
            try
            {
                return await ReadRequestCoreAsync(stream, limit.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new RequestRejectedException(408, $"The request was not received within {readTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds. No action was performed.");
            }
        }

        private static async Task<(string Method, string Target, string Body)?> ReadRequestCoreAsync(Stream stream, CancellationToken token)
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
                string headerName = line[..separator].Trim();
                if (headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && !int.TryParse(line.AsSpan(separator + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
                    throw new RequestRejectedException(400, "Invalid Content-Length. No action was performed.");
                // Only exact Content-Length bodies are read; a chunked body would otherwise be
                // taken as empty and run as {}. 411 tells the client how to retry.
                if (headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    throw new RequestRejectedException(411, "Transfer-Encoding is not supported; send the body with an exact Content-Length. No action was performed.");
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
                if (n <= 0)
                    throw new RequestRejectedException(400, $"Request body ended after {read} of {contentLength} bytes (Content-Length). No action was performed.");
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
    }
}
