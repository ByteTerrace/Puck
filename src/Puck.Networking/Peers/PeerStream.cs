using System.Threading.Channels;

namespace Puck.Networking.Peers;

/// <summary>An ordered byte stream over a peer's authenticated messages. It owns the link and is its only event
/// reader. Writes larger than the message limit are segmented without buffering the whole stream; reads retain
/// at most one message in addition to the link's bounded queue. This lets stream-based application codecs use the
/// same peer identity, encryption, authentication, and backpressure as message-based applications.</summary>
/// <remarks>One reader and one writer may run concurrently. A refused message closes this stream: skipping bytes
/// would corrupt the application's framing. Cancelling a segmented write after it starts closes the link for the
/// same reason. An authenticated empty message ends one direction; use <see cref="CompleteWritesAsync"/> to send
/// it. Ordinary empty writes do nothing. The stream is not seekable and does not support synchronous I/O.</remarks>
public sealed class PeerStream : Stream {
    private readonly PeerLink m_link;
    private readonly SemaphoreSlim m_writeGate = new(
        initialCount: 1,
        maxCount: 1
    );

    private int m_disposed;
    private ReadOnlyMemory<byte> m_pending;
    private bool m_readCompleted;
    private bool m_writeCompleted;

    /// <summary>Creates a stream owning one established link. Do not also consume the link's events or send messages.</summary>
    /// <param name="link">The exclusively owned, established peer link.</param>
    public PeerStream(PeerLink link) => m_link = (link ?? throw new ArgumentNullException(paramName: nameof(link)));

    /// <inheritdoc/>
    public override bool CanRead => (Volatile.Read(location: ref m_disposed) == 0);
    /// <inheritdoc/>
    public override bool CanSeek => false;
    /// <inheritdoc/>
    public override bool CanWrite => CanRead;
    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();
    /// <summary>Gets the authenticated link, for inspecting its remote identity and endpoint.</summary>
    public PeerLink Link => m_link;
    /// <inheritdoc/>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    // Terminal cleanup owns the event reader just like ReadAsync. An authenticated empty message only ends the
    // remote's sending direction; it says nothing about whether that peer has received our final reply. Discard
    // events until the LINK closes (or the caller's deadline expires), including after a previously read EOF.
    internal async Task DrainUntilClosedAsync(CancellationToken ct) {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        m_pending = default;
        m_readCompleted = true;
        while (
            !ct.IsCancellationRequested &&
            await m_link.Events.WaitToReadAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false)
        ) {
            while (
                !ct.IsCancellationRequested &&
                m_link.Events.TryRead(item: out var next)
            ) {
                if (next is PeerEvent.Closed) { return; }
            }
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing) {
        if (disposing) { DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        base.Dispose(disposing: disposing);
    }

    /// <summary>Completes this direction of the stream with an authenticated empty message. Reading the other
    /// direction remains possible. Later writes are refused. Ordinary zero-length writes do not complete it.</summary>
    /// <param name="ct">Cancellation while waiting to send.</param>
    /// <returns>The completion send.</returns>
    public async ValueTask CompleteWritesAsync(CancellationToken ct = default) {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        await m_writeGate.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        try {
            if (m_writeCompleted) { return; }
            await m_link.SendAsync(
                ReadOnlyMemory<byte>.Empty,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);
            m_writeCompleted = true;
        } finally { m_writeGate.Release(); }
    }
    /// <inheritdoc/>
    public override async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) == 0) {
            await m_link.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
        GC.SuppressFinalize(obj: this);
    }
    /// <inheritdoc/>
    public override void Flush() { }
    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(message: "Use asynchronous peer I/O.");
    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(
            buffer: buffer.AsMemory(
                length: count,
                start: offset
            ),
            cancellationToken: cancellationToken
        ).AsTask();
    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        if (
            buffer.IsEmpty ||
            m_readCompleted
        ) { return 0; }
        while (m_pending.IsEmpty) {
            PeerEvent next;

            try { next = await m_link.Events.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false); } catch (ChannelClosedException) { return 0; }
            switch (next) {
                case PeerEvent.Received received:
                    if (received.Payload.IsEmpty) { m_readCompleted = true; return 0; }
                    m_pending = received.Payload;
                    break;
                case PeerEvent.Closed:
                    return 0;
                case PeerEvent.Refused refused:
                    await DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
                    throw new PeerRefusedException(failure: refused.Failure);
            }
        }
        var count = Math.Min(
            val1: buffer.Length,
            val2: m_pending.Length
        );

        m_pending.Span[..count].CopyTo(destination: buffer.Span);
        m_pending = m_pending[count..];
        return count;
    }
    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(message: "Use asynchronous peer I/O.");
    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(
            buffer: buffer.AsMemory(
                length: count,
                start: offset
            ),
            cancellationToken: cancellationToken
        ).AsTask();
    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        await m_writeGate.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var started = false;

        try {
            if (m_writeCompleted) { throw new InvalidOperationException(message: "Peer stream writes have completed."); }
            while (!buffer.IsEmpty) {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(
                    val1: buffer.Length,
                    val2: PeerWireProtocol.MaxMessagePayloadBytes
                );

                started = true;
                await m_link.SendAsync(
                    buffer[..count],
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
                buffer = buffer[count..];
            }
        } catch {
            if (started) { await DisposeAsync().ConfigureAwait(continueOnCapturedContext: false); }
            throw;
        } finally { m_writeGate.Release(); }
    }
}
