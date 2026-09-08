using Puck.Networking;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Puck.Hosting;

/// <summary>An authenticated attachment to one host incarnation. Failure closes it; callers must never replay uncertain work.</summary>
public sealed class LocalControlClient : IControlSession {
    private readonly TcpClient m_socket;
    private readonly NetworkStream m_stream;
    private long m_sequence;
    private int m_busy;
    private int m_disposed;

    private LocalControlClient(TcpClient socket) { m_socket = socket; m_stream = socket.GetStream(); }

    /// <summary>Reads a private host capability and proves both peers know its secret. Connects only to IPv4 loopback.</summary>
    /// <param name="attachmentPath">The host's printed attachment file path, never a URL.</param>
    /// <param name="cancellationToken">Cancels attachment. The connect and handshake each have a five-second ceiling.</param>
    /// <returns>A new dedicated host session.</returns>
    public static async Task<LocalControlClient> ConnectAsync(string attachmentPath, CancellationToken cancellationToken = default) {
        var descriptor = LocalEndpointCapability.ReadDescriptor(attachmentPath);
        var socket = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(IPAddress.Loopback, descriptor.Port, deadline.Token).ConfigureAwait(false);
            await descriptor.AuthenticateAsync(socket.GetStream(), server: false, deadline.Token).ConfigureAwait(false);
            return new(socket);
        } catch { socket.Dispose(); throw; }
    }

    /// <summary>Runs one operation. Concurrent calls refuse immediately; cancellation closes the entire ordered session.</summary>
    /// <param name="operation">exec or capture.</param>
    /// <param name="command">One console line, or null for capture.</param>
    /// <param name="timeoutMilliseconds">Deadline from 1..120000 milliseconds.</param>
    /// <param name="cancellationToken">Cancels queued work; dispatched effects cannot be undone.</param>
    /// <returns>The correlated console result or completed PNG.</returns>
    public async Task<ControlResponse> ExecuteAsync(string operation, string? command, int timeoutMilliseconds = 30_000, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref m_disposed) != 0, this);
        if (Interlocked.CompareExchange(ref m_busy, 1, 0) != 0) { return new(0, "refused", "Attachment busy; await the active operation.", true); }
        try {
            var request = new ControlRequest(checked(m_sequence + 1), operation, command, timeoutMilliseconds);
            if (LocalControlServer.Validate(request) is { } refusal) { return refusal with { Id = 0 }; }
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, ControlJson.Default.ControlRequest);
            if (payload.Length > ControlLimits.RequestBytes) { return new(0, "refused", "Encoded request exceeds 16384 bytes.", true); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMilliseconds);
            try {
                m_sequence = request.Id;
                await ControlWire.WriteAsync(m_stream, ControlWire.RequestKind, payload, ControlLimits.RequestBytes, deadline.Token).ConfigureAwait(false);
                var bytes = await ControlWire.ReadAsync(m_stream, ControlWire.ResponseKind, ControlLimits.ResponseBytes, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Host disconnected; dispatched outcome is unknown.");
                var response = JsonSerializer.Deserialize(bytes.Span, ControlJson.Default.ControlResponse) ?? throw new InvalidDataException("Missing response.");
                if (!response.IsValidFor(request)) { throw new InvalidDataException("Invalid or mismatched control response."); }
                return response;
            } catch { Dispose(); throw; }
        } finally { Volatile.Write(ref m_busy, 0); }
    }

    /// <inheritdoc/>
    Task<ControlResponse> IControlSession.ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request.Operation, request.Command, request.TimeoutMilliseconds, cancellationToken);

    /// <summary>Closes this attachment and its pending ingress. Does not stop the host.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0) { m_socket.Dispose(); }
    }
}
