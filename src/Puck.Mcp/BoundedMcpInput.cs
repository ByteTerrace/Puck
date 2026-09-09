using System.Text;

namespace Puck.Mcp;

// The SDK remains the sole MCP parser. This wrapper owns byte/queue budgets and its underlying stream.
internal sealed class BoundedMcpInput(Stream input, Action ended) : Stream {
    private readonly Decoder m_utf8 = new UTF8Encoding(false, true).GetDecoder();
    private int m_lineBytes;
    private bool m_nonblank;
    private int m_pendingLines;
    private int m_ended;
    private int m_disposed;
    private Exception? m_failure;

    internal Exception? Failure => Volatile.Read(ref m_failure);
    internal void MessageConsumed() => Interlocked.Decrement(ref m_pendingLines);
    internal void Fail(Exception error) {
        Interlocked.CompareExchange(ref m_failure, error, null);
        End();
    }
    private void End() { if (Interlocked.Exchange(ref m_ended, 1) == 0) { ended(); } }

    public override bool CanRead => Volatile.Read(ref m_disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) {
        try {
            var read = input.Read(buffer, offset, count);
            if (count != 0) { Inspect(buffer.AsSpan(offset, read)); }
            return read;
        } catch (IOException error) { if (Volatile.Read(ref m_disposed) == 0) { Fail(error); } throw; }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        try {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (!buffer.IsEmpty) { Inspect(buffer.Span[..read]); }
            return read;
        } catch (IOException error) { if (Volatile.Read(ref m_disposed) == 0) { Fail(error); } throw; }
    }
    private void Inspect(ReadOnlySpan<byte> bytes) {
        try {
            // Decoder state carries a split UTF-8 scalar across reads; EOF flush rejects an unfinished scalar.
            Span<char> characters = stackalloc char[512];
            var remaining = bytes;
            bool completed;
            do {
                m_utf8.Convert(remaining, characters, flush: bytes.IsEmpty, out var consumed, out _, out completed);
                remaining = remaining[consumed..];
            } while (!completed);
            if (bytes.IsEmpty) { End(); }
            foreach (var value in bytes) {
                if (value == (byte)'\n') {
                    if (m_nonblank && Interlocked.Increment(ref m_pendingLines) > 128) {
                        throw new InvalidDataException("MCP input exceeds 128 pending or malformed messages.");
                    }
                    m_lineBytes = 0;
                    m_nonblank = false;
                } else {
                    m_nonblank |= value is not ((byte)' ' or (byte)'\t' or (byte)'\r');
                    if (++m_lineBytes > 64 * 1024) { throw new InvalidDataException("MCP input line exceeds 64 KiB."); }
                }
            }
        } catch (DecoderFallbackException error) {
            var failure = new InvalidDataException("MCP input must be valid UTF-8.", error);
            Fail(failure);
            throw failure;
        } catch (InvalidDataException error) { Fail(error); throw; }
    }

    protected override void Dispose(bool disposing) {
        if (disposing && Interlocked.Exchange(ref m_disposed, 1) == 0) { input.Dispose(); }
        base.Dispose(disposing);
    }
}
