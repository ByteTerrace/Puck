using Puck.Networking;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Puck.Hosting;

/// <summary>Opt-in Windows/Linux x64 local control. A private capability file authenticates up to four loopback sessions.</summary>
public sealed class LocalControlServer : IDisposable {
    private readonly CancellationTokenSource m_stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> m_clients = new();
    private readonly TcpListener m_listener = new(
        localaddr: IPAddress.Loopback,
        port: 0
    );
    // The time a connected peer may take to prove the capability before its connection is closed.
    private static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(seconds: 5);
    // The time a refused request's reply may take to write.
    private static readonly TimeSpan RefusalDeadline = TimeSpan.FromSeconds(seconds: 1);

    private readonly TimeProvider m_clock;
    private readonly Func<IControlSession> m_createSession;
    private readonly LocalEndpointCapability m_descriptor;
    private readonly FileStream m_descriptorFile;

    private int m_disposed;

    /// <summary>Starts listening and creates a unique, user-only attachment file. The host owns this lifetime.</summary>
    /// <param name="createSession">Creates an independent Console ingress after authentication, on a worker.</param>
    /// <param name="clock">Drives each connection's five-second handshake deadline and each request's deadline;
    /// <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <param name="directory">The directory the attachment file is published in, where an adapter following the
    /// newest World looks; <see langword="null"/> is the user's temporary directory.</param>
    public LocalControlServer(Func<IControlSession> createSession, TimeProvider? clock = null, string? directory = null) {
        ArgumentNullException.ThrowIfNull(createSession);
        m_clock = (clock ?? TimeProvider.System);
        m_createSession = createSession;
        var host = Guid.NewGuid().ToString(format: "N");

        AttachmentPath = Path.Combine(
            path1: (directory ?? Path.GetTempPath()),
            path2: $"puck-control-{host}.json"
        );
        try {
            m_listener.Start(backlog: 4);
            m_descriptor = new(port: ((IPEndPoint)m_listener.LocalEndpoint).Port);
            m_descriptorFile = m_descriptor.WriteDescriptor(path: AttachmentPath);
        } catch { m_listener.Stop(); m_stop.Dispose(); throw; }
        _ = AcceptAsync();
    }

    /// <summary>The capability file path. Its contents must never be logged or put in process arguments.</summary>
    public string AttachmentPath { get; }

    private async Task AcceptAsync() {
        try {
            while (!m_stop.IsCancellationRequested) {
                var client = await m_listener.AcceptTcpClientAsync(cancellationToken: m_stop.Token).ConfigureAwait(continueOnCapturedContext: false);

                if (m_clients.Count >= 4) { client.Dispose(); continue; }
                client.NoDelay = true;
                m_clients.TryAdd(
                    key: client,
                    value: 0
                );
                _ = ServeAsync(client: client);
            }
        } catch (Exception error) when ((error is OperationCanceledException or SocketException or ObjectDisposedException)) {
            // Stop closes the accept socket. A transport failure cannot stop the world.
        }
    }
    private static async Task<ControlResponse> ExecuteSessionAsync(IControlSession session, ControlRequest request, CancellationToken token) {
        Task<ControlResponse>? work = null;

        try {
            work = session.ExecuteAsync(
                cancellationToken: token,
                request: request
            );
            // A deadline is enforced at this boundary, even if an injected session ignores cancellation.
            return await work.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) {
            if (work is not null) { _ = ObserveAbandonedAsync(work: work); }
            throw;
        } catch (Exception error) {
            return new(
                request.Id,
                "unknown",
                $"Host operation failed; effects may already have occurred. {error.Message}",
                true
            );
        }
    }
    private static bool IsConnectionError(Exception error) => (error is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException or JsonException or UnauthorizedAccessException or ArgumentException or OverflowException);
    private static async Task ObserveAbandonedAsync(Task work) {
        try { await work.ConfigureAwait(continueOnCapturedContext: false); } catch (Exception) { /* The ingress is closed; observe a late failure without replaying work. */ }
    }
    private async Task ServeAsync(TcpClient client) {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: m_stop.Token)) {
            Task<ReadOnlyMemory<byte>?>? next = null;

            try {
                var stream = client.GetStream();

                using (var handshake = new OperationDeadline(
                    caller: lifetime.Token,
                    timeout: HandshakeDeadline,
                    timeProvider: m_clock
                )) {
                    await m_descriptor.AuthenticateAsync(
                        stream,
                        server: true,
                        handshake.Token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
                using var session = m_createSession();
                var sequence = 0L;

                next = ControlWire.ReadAsync(
                    stream,
                    ControlWire.RequestKind,
                    ControlLimits.RequestBytes,
                    lifetime.Token
                );
                while (await next.ConfigureAwait(continueOnCapturedContext: false) is { } payload) {
                    var request = (JsonSerializer.Deserialize(
                        payload.Span,
                        ControlJson.Default.ControlRequest
                    ) ?? throw new InvalidDataException(message: "Missing request."));

                    if (request.Id != checked(++sequence)) { throw new InvalidDataException(message: "Duplicate or out-of-order request ID."); }
                    var refusal = Validate(request);
                    using var deadline = new OperationDeadline(
                        caller: lifetime.Token,
                        timeout: ((refusal is null)
                            ? TimeSpan.FromMilliseconds(value: request.TimeoutMilliseconds)
                            : RefusalDeadline),
                        timeProvider: m_clock
                    );

                    // Keep one bounded read outstanding to observe disconnect while the pump is waiting.
                    // Pipelining is forbidden: it closes the ingress instead of growing the text queue.
                    next = ControlWire.ReadAsync(
                        stream,
                        ControlWire.RequestKind,
                        ControlLimits.RequestBytes,
                        lifetime.Token
                    );
                    var operation = ((refusal is null)
                        ? ExecuteSessionAsync(
                            session,
                            request,
                            deadline.Token
                        )
                        : Task.FromResult(result: refusal)
                    );

                    if (await Task.WhenAny(
                        task1: operation,
                        task2: next
                    ).ConfigureAwait(continueOnCapturedContext: false) == next) {
                        lifetime.Cancel();
                        try { await operation.ConfigureAwait(continueOnCapturedContext: false); } catch (Exception error) when (IsConnectionError(error: error)) { }
                        return;
                    }
                    var response = await operation.ConfigureAwait(continueOnCapturedContext: false);

                    if (
                        (response is null) ||
                        !response.IsValidFor(request)
                    ) {
                        response = new(
                            request.Id,
                            "unknown",
                            "Operation ran but its result is invalid or exceeds its limit; inspect state before any retry.",
                            true
                        );
                    }
                    await ControlWire.WriteAsync(
                        stream,
                        ControlWire.ResponseKind,
                        JsonSerializer.SerializeToUtf8Bytes(
                            response,
                            ControlJson.Default.ControlResponse
                        ),
                        ControlLimits.ResponseBytes,
                        deadline.Token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
            } catch (Exception error) when (IsConnectionError(error: error)) {
                // A deadline, malformed frame, or disconnect invalidates this ingress. No uncertain action is replayed.
            } finally {
                lifetime.Cancel();
                client.Dispose();
                if (next is not null) {
                    try { await next.ConfigureAwait(continueOnCapturedContext: false); } catch (Exception error) when (IsConnectionError(error: error)) { }
                }
                m_clients.TryRemove(
                    key: client,
                    value: out _
                );
            }
        }
    }

    /// <summary>Closes listener and session sockets without stopping World or waiting on its pump.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) { return; }
        m_stop.Cancel();
        m_listener.Stop();
        foreach (var client in m_clients.Keys) { client.Dispose(); }
        m_descriptorFile.Dispose();
        try { File.Delete(path: AttachmentPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        // The cancellation source remains alive until the accept and connection continuations observe it.
    }
    /// <summary>Validates the operation before it can reach an unbounded host queue.</summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>A refusal, or null when admissible.</returns>
    public static ControlResponse? Validate(ControlRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        string? error = null;

        if (request.TimeoutMilliseconds is < 1 or > ControlLimits.TimeoutMilliseconds) { error = "Deadline must be 1..120000 milliseconds."; } else if (request.Operation == "capture") { if (request.Command is not null) { error = "Capture accepts no command or path."; } } else if (request.Operation != "exec") { error = "Unknown control operation."; } else if (
            string.IsNullOrWhiteSpace(value: request.Command) ||
            (request.Command.AsSpan().TrimStart()[0] == '#') ||
            (request.Command.IndexOfAny(anyOf: ['\r', '\n', '\0']) >= 0)
        ) { error = "Exec requires one nonblank, noncomment console line."; } else if (request.Command.Length > 8192) { error = "Console line exceeds 8192 characters."; }
        return ((error is null)
            ? null
            : new(
                request.Id,
                "refused",
                error,
                true
            )
        );
    }
}
