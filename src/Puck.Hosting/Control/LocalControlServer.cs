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
    private readonly TcpListener m_listener = new(IPAddress.Loopback, 0);
    private readonly Func<IControlSession> m_createSession;
    private readonly LocalEndpointCapability m_descriptor;
    private readonly FileStream m_descriptorFile;
    private int m_disposed;

    /// <summary>Starts listening and creates a unique, user-only attachment file. The host owns this lifetime.</summary>
    /// <param name="createSession">Creates an independent Console ingress after authentication, on a worker.</param>
    public LocalControlServer(Func<IControlSession> createSession) {
        ArgumentNullException.ThrowIfNull(createSession);
        m_createSession = createSession;
        var host = Guid.NewGuid().ToString("N");
        AttachmentPath = Path.Combine(Path.GetTempPath(), $"puck-control-{host}.json");
        try {
            m_listener.Start(backlog: 4);
            m_descriptor = new(((IPEndPoint)m_listener.LocalEndpoint).Port);
            m_descriptorFile = m_descriptor.WriteDescriptor(AttachmentPath);
        } catch { m_listener.Stop(); m_stop.Dispose(); throw; }
        _ = AcceptAsync();
    }

    /// <summary>The capability file path. Its contents must never be logged or put in process arguments.</summary>
    public string AttachmentPath { get; }

    private async Task AcceptAsync() {
        try {
            while (!m_stop.IsCancellationRequested) {
                var client = await m_listener.AcceptTcpClientAsync(m_stop.Token).ConfigureAwait(false);
                if (m_clients.Count >= 4) { client.Dispose(); continue; }
                client.NoDelay = true;
                m_clients.TryAdd(client, 0);
                _ = ServeAsync(client);
            }
        } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) {
            // Stop closes the accept socket. A transport failure cannot stop the world.
        }
    }

    private async Task ServeAsync(TcpClient client) {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(m_stop.Token)) {
            Task<ReadOnlyMemory<byte>?>? next = null;
            try {
                var stream = client.GetStream();
                await m_descriptor.AuthenticateAsync(stream, server: true, lifetime.Token).ConfigureAwait(false);
                using var session = m_createSession();
                long sequence = 0;
                next = ControlWire.ReadAsync(stream, ControlWire.RequestKind, ControlLimits.RequestBytes, lifetime.Token);
                while (await next.ConfigureAwait(false) is { } payload) {
                    var request = JsonSerializer.Deserialize(payload.Span, ControlJson.Default.ControlRequest) ?? throw new InvalidDataException("Missing request.");
                    if (request.Id != checked(++sequence)) { throw new InvalidDataException("Duplicate or out-of-order request ID."); }
                    var refusal = Validate(request);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(refusal is null ? request.TimeoutMilliseconds : 1000);
                    // Keep one bounded read outstanding to observe disconnect while the pump is waiting.
                    // Pipelining is forbidden: it closes the ingress instead of growing the text queue.
                    next = ControlWire.ReadAsync(stream, ControlWire.RequestKind, ControlLimits.RequestBytes, lifetime.Token);
                    var operation = refusal is null ? ExecuteSessionAsync(session, request, deadline.Token) : Task.FromResult(refusal);
                    if (await Task.WhenAny(operation, next).ConfigureAwait(false) == next) {
                        lifetime.Cancel();
                        try { await operation.ConfigureAwait(false); } catch (Exception error) when (IsConnectionError(error)) { }
                        return;
                    }
                    var response = await operation.ConfigureAwait(false);
                    if (response is null || !response.IsValidFor(request)) {
                        response = new(request.Id, "unknown", "Operation ran but its result is invalid or exceeds its limit; inspect state before any retry.", true);
                    }
                    await ControlWire.WriteAsync(stream, ControlWire.ResponseKind, JsonSerializer.SerializeToUtf8Bytes(response, ControlJson.Default.ControlResponse), ControlLimits.ResponseBytes, deadline.Token).ConfigureAwait(false);
                }
            } catch (Exception error) when (IsConnectionError(error)) {
                // A deadline, malformed frame, or disconnect invalidates this ingress. No uncertain action is replayed.
            } finally {
                lifetime.Cancel();
                client.Dispose();
                if (next is not null) {
                    try { await next.ConfigureAwait(false); } catch (Exception error) when (IsConnectionError(error)) { }
                }
                m_clients.TryRemove(client, out _);
            }
        }
    }

    private static async Task<ControlResponse> ExecuteSessionAsync(IControlSession session, ControlRequest request, CancellationToken token) {
        Task<ControlResponse>? work = null;
        try {
            work = session.ExecuteAsync(request, token);
            // A deadline is enforced at this boundary, even if an injected session ignores cancellation.
            return await work.WaitAsync(token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            if (work is not null) { _ = ObserveAbandonedAsync(work); }
            throw;
        } catch (Exception error) {
            return new(request.Id, "unknown", $"Host operation failed; effects may already have occurred. {error.Message}", true);
        }
    }

    private static async Task ObserveAbandonedAsync(Task work) {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { /* The ingress is closed; observe a late failure without replaying work. */ }
    }

    private static bool IsConnectionError(Exception error) => error is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException or JsonException or UnauthorizedAccessException or ArgumentException or OverflowException;

    /// <summary>Validates the operation before it can reach an unbounded host queue.</summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>A refusal, or null when admissible.</returns>
    public static ControlResponse? Validate(ControlRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        string? error = null;
        if (request.TimeoutMilliseconds is < 1 or > ControlLimits.TimeoutMilliseconds) { error = "Deadline must be 1..120000 milliseconds."; }
        else if (request.Operation == "capture") { if (request.Command is not null) { error = "Capture accepts no command or path."; } }
        else if (request.Operation != "exec") { error = "Unknown control operation."; }
        else if (string.IsNullOrWhiteSpace(request.Command) || request.Command.AsSpan().TrimStart()[0] == '#' || request.Command.IndexOfAny(['\r', '\n', '\0']) >= 0) { error = "Exec requires one nonblank, noncomment console line."; }
        else if (request.Command.Length > 8192) { error = "Console line exceeds 8192 characters."; }
        return error is null ? null : new(request.Id, "refused", error, true);
    }

    /// <summary>Closes listener and session sockets without stopping World or waiting on its pump.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0) { return; }
        m_stop.Cancel();
        m_listener.Stop();
        foreach (var client in m_clients.Keys) { client.Dispose(); }
        m_descriptorFile.Dispose();
        try { File.Delete(AttachmentPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        // The cancellation source remains alive until the accept and connection continuations observe it.
    }
}
