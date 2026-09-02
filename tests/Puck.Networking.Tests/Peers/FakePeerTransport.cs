using System.Net;
using System.Threading.Channels;
using Puck.Networking.Peers;

namespace Puck.Networking.Tests.Peers;

/// <summary>An <see cref="IPeerTransport"/> with no socket beneath it. A law hands it the connections its listener
/// accepts (<see cref="Accept"/>) and the connection every dial returns, so a <see cref="Peer"/> can be driven
/// against behaviour loopback QUIC never exhibits on its own — a connection that never opens a control stream, a
/// stream that swallows every write and never answers (<see cref="SilentPeerConnection"/>), or a remote that never
/// acknowledges a stream shutdown (<see cref="InMemoryPeerConnection"/>).</summary>
internal sealed class FakePeerTransport : IPeerTransport {
    private readonly Channel<IPeerConnection> m_accepted = Channel.CreateUnbounded<IPeerConnection>();

    private readonly Func<EndPoint, IPeerConnection> m_dial;

    private readonly List<IPeerConnection> m_taken = [];
    private readonly Lock m_takenLock = new();

    /// <summary>Initializes the transport.</summary>
    /// <param name="dial">Produces the connection <see cref="DialAsync"/> returns for an endpoint.</param>
    public FakePeerTransport(Func<EndPoint, IPeerConnection> dial) {
        m_dial = dial;
    }

    /// <summary>Gets a snapshot of every connection a listener has handed to its caller so far, in order — the
    /// connections the peer's accept loop took and so owns, as opposed to ones still queued or offered after the
    /// listener closed and dropped.</summary>
    public IReadOnlyList<IPeerConnection> Taken {
        get {
            lock (m_takenLock) {
                return [.. m_taken];
            }
        }
    }

    private void Took(IPeerConnection connection) {
        lock (m_takenLock) {
            m_taken.Add(item: connection);
        }
    }

    /// <summary>Hands the listener one connection to accept next, as if a remote side had just completed the
    /// transport's own handshake. Dropped once the listener has closed, as a transport drops a connection nobody
    /// accepts.</summary>
    /// <param name="connection">The connection the listener yields.</param>
    public void Accept(IPeerConnection connection) => m_accepted.Writer.TryWrite(item: connection);
    public ValueTask<IPeerConnection> DialAsync(EndPoint endpoint, CancellationToken ct = default) => ValueTask.FromResult(result: m_dial(arg: endpoint));
    public ValueTask<IPeerListener> ListenAsync(IPEndPoint endpoint, CancellationToken ct = default) => ValueTask.FromResult<IPeerListener>(result: new Listener(
        accepted: m_accepted,
        endpoint: endpoint,
        took: Took
    ));
    public ValueTask DisposeAsync() {
        m_accepted.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }

    private sealed class Listener : IPeerListener {
        private readonly Channel<IPeerConnection> m_accepted;
        private readonly Action<IPeerConnection> m_took;

        public Listener(Channel<IPeerConnection> accepted, IPEndPoint endpoint, Action<IPeerConnection> took) {
            m_accepted = accepted;
            m_took = took;

            LocalEndpoint = endpoint;
        }

        public IPEndPoint LocalEndpoint { get; }

        public async ValueTask<IPeerConnection?> AcceptAsync(CancellationToken ct = default) {
            IPeerConnection connection;

            try {
                connection = await m_accepted.Reader.ReadAsync(cancellationToken: ct);
            } catch (ChannelClosedException) {
                return null;
            }

            m_took(obj: connection);

            return connection;
        }
        public ValueTask DisposeAsync() {
            m_accepted.Writer.TryComplete();

            return ValueTask.CompletedTask;
        }
    }
}
/// <summary>A connection that never yields a stream to an acceptor and hands a dialer a stream that never answers:
/// <see cref="AcceptStreamAsync"/> waits until it is cancelled, and every stream <see cref="OpenStreamAsync"/> opens
/// accepts each write and blocks each read until cancelled — the two shapes the handshake-deadline laws need. A
/// transport key is present so the fault, when one is provoked, is the silence and never an unbound channel.</summary>
internal sealed class SilentPeerConnection : IPeerConnection {
    private readonly CancellationTokenSource m_closed = new();

    private int m_disposed;

    /// <summary>Gets a value indicating whether <see cref="DisposeAsync"/> ran — what a law asserts to show a
    /// failed handshake released its connection.</summary>
    public bool IsDisposed => (Volatile.Read(location: ref m_disposed) != 0);
    public int MaxDatagramBytes => 0;

    public EndPoint RemoteEndpoint { get; } = PeerTestSupport.Loopback(port: 1);
    public ReadOnlyMemory<byte> RemoteTransportKey { get; } = "a key this connection never has to prove"u8.ToArray();

    public async ValueTask<Stream?> AcceptStreamAsync(CancellationToken ct = default) {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(
            token1: ct,
            token2: m_closed.Token
        );

        try {
            await Task.Delay(
                cancellationToken: wait.Token,
                delay: Timeout.InfiniteTimeSpan
            );
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            // Disposed rather than cancelled: the connection closed before any stream was opened.
            return null;
        }

        return null;
    }
    public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) => ValueTask.FromResult<Stream>(result: new SilentStream(closed: m_closed.Token));
    public ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken ct = default) => throw new NotSupportedException(message: "the silent connection carries no datagrams");
    public ValueTask SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => throw new NotSupportedException(message: "the silent connection carries no datagrams");
    public ValueTask DisposeAsync() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) == 0) {
            m_closed.Cancel();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Accepts every write and completes no read: a read waits until its own token is cancelled (and
    /// throws) or the connection closes (and reports end of stream).</summary>
    private sealed class SilentStream : Stream {
        private readonly CancellationToken m_closed;

        public SilentStream(CancellationToken closed) {
            m_closed = closed;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() {
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(message: "the silent stream is read asynchronously only");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(
            buffer: buffer.AsMemory(
                length: count,
                start: offset
            ),
            cancellationToken: cancellationToken
        ).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                token1: cancellationToken,
                token2: m_closed
            );

            try {
                await Task.Delay(
                    cancellationToken: wait.Token,
                    delay: Timeout.InfiniteTimeSpan
                );
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                return 0;
            }

            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) {
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
/// <summary>One end of an in-memory connection pair (<see cref="Pair"/>): every stream one end opens is accepted
/// by the other, bytes cross through channels, and the two ends carry each other's proven key, so two
/// <see cref="Peer"/>s over <see cref="FakePeerTransport"/> complete a real handshake with no socket beneath them.
/// It models two transport facts loopback QUIC never shows on cue: a stream's graceful shutdown completes only once
/// the remote side acknowledges it (<see cref="AcknowledgeShutdowns"/>, which a law standing in for a vanished
/// remote never calls) or the connection beneath it is disposed, and a stream write completes only once the remote
/// side has granted flow-control credit for it (<see cref="WithholdWriteCredit"/> models a remote that never
/// does).</summary>
internal sealed class InMemoryPeerConnection : IPeerConnection {
    private readonly TaskCompletionSource m_closed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource m_closing = new();
    private readonly Channel<Stream> m_inboundStreams = Channel.CreateUnbounded<Stream>();
    private readonly TaskCompletionSource m_shutdownsAcknowledged = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<InMemoryStream> m_streams = [];
    private readonly Lock m_streamsLock = new();
    private InMemoryPeerConnection m_remote = null!;

    private int m_writeCreditWithheld;

    private InMemoryPeerConnection(EndPoint remoteEndpoint, ReadOnlyMemory<byte> remoteTransportKey) {
        RemoteEndpoint = remoteEndpoint;
        RemoteTransportKey = remoteTransportKey;
    }

    /// <summary>Gets a value indicating whether <see cref="DisposeAsync"/> ran.</summary>
    public bool IsDisposed => m_closed.Task.IsCompleted;
    /// <summary>Gets a value indicating whether the remote side has stopped granting this end's stream writes any
    /// flow-control credit (<see cref="WithholdWriteCredit"/>).</summary>
    public bool IsWriteCreditWithheld => (Volatile.Read(location: ref m_writeCreditWithheld) != 0);
    public int MaxDatagramBytes => 0;
    public EndPoint RemoteEndpoint { get; }
    public ReadOnlyMemory<byte> RemoteTransportKey { get; }

    /// <summary>Creates the two ends of one connection. End A's <see cref="RemoteTransportKey"/> is
    /// <paramref name="keyProvedByB"/> and end B's is <paramref name="keyProvedByA"/>, as if each had proved its own
    /// key to the other at the transport's handshake.</summary>
    /// <param name="keyProvedByA">The key end A proved.</param>
    /// <param name="keyProvedByB">The key end B proved.</param>
    /// <returns>Both ends.</returns>
    public static (InMemoryPeerConnection A, InMemoryPeerConnection B) Pair(ReadOnlyMemory<byte> keyProvedByA, ReadOnlyMemory<byte> keyProvedByB) {
        var a = new InMemoryPeerConnection(
            remoteEndpoint: PeerTestSupport.Loopback(port: 2),
            remoteTransportKey: keyProvedByB
        );
        var b = new InMemoryPeerConnection(
            remoteEndpoint: PeerTestSupport.Loopback(port: 1),
            remoteTransportKey: keyProvedByA
        );

        a.m_remote = b;
        b.m_remote = a;

        return (a, b);
    }
    /// <summary>Lets every stream shutdown this end started complete, as a live remote would by acknowledging it.</summary>
    public void AcknowledgeShutdowns() => m_shutdownsAcknowledged.TrySetResult();
    /// <summary>Stands in for a remote that keeps the connection alive but never grants another byte of stream
    /// flow-control credit: from now on every write on a stream this end owns blocks until the write's own token is
    /// cancelled (throwing <see cref="OperationCanceledException"/>, as a transport's aborted write does) or this
    /// connection is disposed (throwing <see cref="IOException"/>).</summary>
    public void WithholdWriteCredit() => Volatile.Write(
        location: ref m_writeCreditWithheld,
        value: 1
    );
    public async ValueTask<Stream?> AcceptStreamAsync(CancellationToken ct = default) {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(
            token1: ct,
            token2: m_closing.Token
        );

        try {
            return await m_inboundStreams.Reader.ReadAsync(cancellationToken: wait.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return null;
        } catch (ChannelClosedException) {
            return null;
        }
    }
    public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) {
        var toRemote = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var fromRemote = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var local = new InMemoryStream(
            inbox: fromRemote,
            outbox: toRemote,
            owner: this
        );
        var remote = new InMemoryStream(
            inbox: toRemote,
            outbox: fromRemote,
            owner: m_remote
        );

        lock (m_streamsLock) {
            m_streams.Add(item: local);
        }

        lock (m_remote.m_streamsLock) {
            m_remote.m_streams.Add(item: remote);
        }

        if (!m_remote.m_inboundStreams.Writer.TryWrite(item: remote)) {
            throw new IOException(message: "the remote end of the in-memory connection is closed");
        }

        return ValueTask.FromResult<Stream>(result: local);
    }
    public ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken ct = default) => throw new NotSupportedException(message: "the in-memory connection carries no datagrams");
    public ValueTask SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => throw new NotSupportedException(message: "the in-memory connection carries no datagrams");
    public ValueTask DisposeAsync() {
        if (!m_closed.TrySetResult()) {
            return ValueTask.CompletedTask;
        }

        m_closing.Cancel();
        m_inboundStreams.Writer.TryComplete();

        InMemoryStream[] streams;

        lock (m_streamsLock) {
            streams = [.. m_streams];
        }

        // A closed connection ends every stream on it in both directions, the way a transport's connection close
        // fails the remote's reads as well as this side's.
        foreach (var stream in streams) {
            stream.Abort();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>One end of an in-memory stream: writes go to the outbox, reads drain the inbox, end of stream is the
    /// inbox completing, and disposal completes the outbox (the remote reads end of stream) and then waits for the
    /// owning connection's shutdown acknowledgement or its closure, whichever comes first.</summary>
    private sealed class InMemoryStream : Stream {
        private readonly Channel<ReadOnlyMemory<byte>> m_inbox;
        private readonly Channel<ReadOnlyMemory<byte>> m_outbox;
        private readonly InMemoryPeerConnection m_owner;

        private ReadOnlyMemory<byte> m_pending;

        public InMemoryStream(Channel<ReadOnlyMemory<byte>> inbox, Channel<ReadOnlyMemory<byte>> outbox, InMemoryPeerConnection owner) {
            m_inbox = inbox;
            m_outbox = outbox;
            m_owner = owner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private async ValueTask DisposeCoreAsync() {
            m_outbox.Writer.TryComplete();

            await Task.WhenAny(
                task1: m_owner.m_shutdownsAcknowledged.Task,
                task2: m_owner.m_closed.Task
            );
        }

        /// <summary>Ends the stream in both directions without waiting for anything.</summary>
        public void Abort() {
            m_outbox.Writer.TryComplete();
            m_inbox.Writer.TryComplete();
        }
        public override ValueTask DisposeAsync() => DisposeCoreAsync();
        public override void Flush() {
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(message: "the in-memory stream is read asynchronously only");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(
            buffer: buffer.AsMemory(
                length: count,
                start: offset
            ),
            cancellationToken: cancellationToken
        ).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            if (m_pending.IsEmpty) {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                    token1: cancellationToken,
                    token2: m_owner.m_closing.Token
                );

                try {
                    m_pending = await m_inbox.Reader.ReadAsync(cancellationToken: wait.Token);
                } catch (ChannelClosedException) {
                    return 0;
                } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                    throw new IOException(message: "the in-memory connection was closed under a read");
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
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(message: "the in-memory stream is written asynchronously only");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(
            buffer: buffer.AsMemory(
                length: count,
                start: offset
            ),
            cancellationToken: cancellationToken
        ).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();

            if (m_owner.IsWriteCreditWithheld) {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                    token1: cancellationToken,
                    token2: m_owner.m_closing.Token
                );

                try {
                    await Task.Delay(
                        cancellationToken: wait.Token,
                        delay: Timeout.InfiniteTimeSpan
                    );
                } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                    throw new IOException(message: "the in-memory connection was closed under a write");
                }
            }

            if (
                buffer.IsEmpty ||
                m_outbox.Writer.TryWrite(item: buffer.ToArray())
            ) {
                return;
            }

            throw new IOException(message: "the in-memory stream is closed");
        }
    }
}
