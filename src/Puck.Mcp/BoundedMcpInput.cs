using System.Text;

namespace Puck.Mcp;

// The SDK remains the sole MCP parser. This wrapper owns byte/queue budgets and its underlying stream.
internal sealed class BoundedMcpInput(Stream input, Action ended) : Stream {
    private readonly Decoder m_utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    ).GetDecoder();

    private int m_disposed;
    private int m_ended;
    private Exception? m_failure;
    private int m_lineBytes;
    private bool m_nonblank;
    private int m_pendingLines;

    internal Exception? Failure => Volatile.Read(location: ref m_failure);

    public override bool CanRead => (Volatile.Read(location: ref m_disposed) == 0);
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    internal void Fail(Exception error) {
        Interlocked.CompareExchange(
            comparand: null,
            location1: ref m_failure,
            value: error
        );
        End();
    }
    internal void MessageConsumed() => Interlocked.Decrement(location: ref m_pendingLines);

    private void End() {
        if (Interlocked.Exchange(
        location1: ref m_ended,
        value: 1
    ) == 0) { ended(); }
    }
    private void Inspect(ReadOnlySpan<byte> bytes) {
        try {
            // Decoder state carries a split UTF-8 scalar across reads; EOF flush rejects an unfinished scalar.
            Span<char> characters = stackalloc char[512];
            var remaining = bytes;
            bool completed;

            do {
                m_utf8.Convert(
                    remaining,
                    characters,
                    flush: bytes.IsEmpty,
                    out var consumed,
                    out _,
                    out completed
                );
                remaining = remaining[consumed..];
            } while (!completed);
            if (bytes.IsEmpty) { End(); }
            foreach (var value in bytes) {
                if (value == ((byte)'\n')) {
                    if (
                        m_nonblank &&
                        (Interlocked.Increment(location: ref m_pendingLines) > 128)
                    ) {
                        throw new InvalidDataException(message: "MCP input exceeds 128 pending or malformed messages.");
                    }
                    m_lineBytes = 0;
                    m_nonblank = false;
                } else {
                    m_nonblank |= (value is not (((byte)' ') or ((byte)'\t') or ((byte)'\r')));
                    if (++m_lineBytes > (64 * 1024)) { throw new InvalidDataException(message: "MCP input line exceeds 64 KiB."); }
                }
            }
        } catch (DecoderFallbackException error) {
            var failure = new InvalidDataException(
                innerException: error,
                message: "MCP input must be valid UTF-8."
            );

            Fail(error: failure);
            throw failure;
        } catch (InvalidDataException error) { Fail(error: error); throw; }
    }

    protected override void Dispose(bool disposing) {
        if (
            disposing &&
            (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) == 0)
        ) { input.Dispose(); }
        base.Dispose(disposing: disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) {
        try {
            var read = input.Read(
                buffer: buffer,
                count: count,
                offset: offset
            );

            if (count != 0) {
                Inspect(bytes: buffer.AsSpan(
                length: read,
                start: offset
            ));
            }
            return read;
        } catch (IOException error) { if (Volatile.Read(location: ref m_disposed) == 0) { Fail(error: error); } throw; }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(
        buffer: buffer.AsMemory(
            length: count,
            start: offset
        ),
        cancellationToken: cancellationToken
    ).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        try {
            var read = await input.ReadAsync(
                buffer: buffer,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!buffer.IsEmpty) { Inspect(bytes: buffer.Span[..read]); }
            return read;
        } catch (IOException error) { if (Volatile.Read(location: ref m_disposed) == 0) { Fail(error: error); } throw; }
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
