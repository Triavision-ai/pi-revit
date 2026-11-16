using System.Text;
using Xunit;

namespace RevitBridge.Tests;

/// <summary>The bridge's HTTP framing: status names and reading one request from a connection.</summary>
public class HttpWireTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>A connection that delivers the given pieces one read at a time, then ends.</summary>
    private sealed class PiecewiseStream(params byte[][] pieces) : Stream
    {
        private readonly Queue<byte[]> _pieces = new(pieces);
        private byte[] _current = Array.Empty<byte>();
        private int _offset;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _current.Length)
            {
                if (_pieces.Count == 0) return ValueTask.FromResult(0);
                _current = _pieces.Dequeue();
                _offset = 0;
            }
            int count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A client that connects, optionally sends a beginning, and then stays silent.</summary>
    private sealed class SilentStream(byte[]? beginning = null) : Stream
    {
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent && beginning != null)
            {
                _sent = true;
                beginning.CopyTo(buffer);
                return beginning.Length;
            }
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static Task<(string Method, string Target, string Body)?> Read(Stream stream)
        => HttpWire.ReadRequestAsync(stream, Generous, CancellationToken.None);

    // ------------------------------------------------------------ status names

    [Theory]
    [InlineData(200, "OK")]
    [InlineData(400, "Bad Request")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "Not Found")]
    [InlineData(405, "Method Not Allowed")]
    [InlineData(408, "Request Timeout")]
    [InlineData(409, "Conflict")]
    [InlineData(411, "Length Required")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(503, "Service Unavailable")]
    public void Every_status_the_bridge_sends_has_its_own_name(int status, string reason)
        => Assert.Equal(reason, HttpWire.ReasonPhrase(status));

    [Fact]
    public void No_error_status_is_ever_called_OK()
    {
        // 0.7.0 sent "408 OK" and "503 OK".
        for (int status = 400; status < 600; status++)
            Assert.NotEqual("OK", HttpWire.ReasonPhrase(status));
    }

    // ------------------------------------------------------------ reading a request

    [Fact]
    public async Task A_get_request_is_read_without_a_body()
    {
        var request = await Read(new PiecewiseStream(Bytes("GET /ping?token=abc HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")));

        Assert.Equal(("GET", "/ping?token=abc", ""), request);
    }

    [Fact]
    public async Task A_body_is_read_by_its_exact_byte_length()
    {
        // Content-Length counts bytes, not characters: the Japanese name takes three bytes per character.
        string body = "{\"name\":\"サンプル構造\"}";
        byte[] bodyBytes = Bytes(body);
        var stream = new PiecewiseStream(Bytes($"POST /tools/x/execute?token=abc HTTP/1.1\r\nContent-Length: {bodyBytes.Length}\r\n\r\n"), bodyBytes);

        var request = await Read(stream);

        Assert.Equal(("POST", "/tools/x/execute?token=abc", body), request);
    }

    [Fact]
    public async Task A_request_arriving_in_small_pieces_is_put_together()
    {
        byte[] all = Bytes("POST /tools/x/execute?token=abc HTTP/1.1\r\nContent-Length: 13\r\n\r\n{\"value\":123}");
        var pieces = all.Chunk(7).ToArray();

        var request = await Read(new PiecewiseStream(pieces));

        Assert.Equal(("POST", "/tools/x/execute?token=abc", "{\"value\":123}"), request);
    }

    [Fact]
    public async Task A_connection_closed_without_a_request_is_not_an_error()
        => Assert.Null(await Read(new PiecewiseStream()));

    [Fact]
    public async Task A_chunked_body_is_rejected_before_anything_runs()
    {
        var stream = new PiecewiseStream(Bytes("POST /tools/x/execute HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n"));

        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => Read(stream));

        Assert.Equal(411, error.Status);
    }

    [Fact]
    public async Task A_content_length_that_is_not_a_number_is_rejected()
    {
        var stream = new PiecewiseStream(Bytes("POST /tools/x/execute HTTP/1.1\r\nContent-Length: twelve\r\n\r\n"));

        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => Read(stream));

        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task A_body_shorter_than_announced_is_rejected()
    {
        var stream = new PiecewiseStream(Bytes("POST /tools/x/execute HTTP/1.1\r\nContent-Length: 50\r\n\r\n{\"cut\":"));

        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => Read(stream));

        Assert.Equal(400, error.Status);
        Assert.Contains("No action was performed", error.Message);
    }

    // ------------------------------------------------------------ read timeout

    [Fact]
    public async Task A_client_that_connects_and_sends_nothing_is_timed_out()
    {
        // 0.7.0 waited on such a connection forever.
        var error = await Assert.ThrowsAsync<RequestRejectedException>(
            () => HttpWire.ReadRequestAsync(new SilentStream(), TimeSpan.FromMilliseconds(150), CancellationToken.None));

        Assert.Equal(408, error.Status);
    }

    [Fact]
    public async Task A_client_that_stops_in_the_middle_of_a_request_is_timed_out()
    {
        var stream = new SilentStream(Bytes("POST /tools/x/execute HTTP/1.1\r\nContent-Length: 50\r\n\r\n{\"half\":"));

        var error = await Assert.ThrowsAsync<RequestRejectedException>(
            () => HttpWire.ReadRequestAsync(stream, TimeSpan.FromMilliseconds(150), CancellationToken.None));

        Assert.Equal(408, error.Status);
    }

    [Fact]
    public async Task Bridge_shutdown_is_not_reported_as_a_client_timeout()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpWire.ReadRequestAsync(new SilentStream(), Generous, shutdown.Token));
    }
}
