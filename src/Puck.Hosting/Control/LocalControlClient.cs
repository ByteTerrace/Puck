using Puck.Networking;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Puck.Hosting;

/// <summary>An authenticated attachment to one host incarnation. Failure closes it; callers must never replay uncertain work.</summary>
public sealed class LocalControlClient : IControlSession {
    // The time a connect and its capability handshake may take before the host is treated as unreachable.
    private static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(seconds: 5);

    private readonly TimeProvider m_clock;
    private readonly TcpClient m_socket;
    private readonly NetworkStream m_stream;

    private int m_busy;
    private int m_disposed;
    private long m_sequence;

    private LocalControlClient(TcpClient socket, TimeProvider clock) { m_clock = clock; m_socket = socket; m_stream = socket.GetStream(); }

    /// <summary>Gets whether this attachment is closed: disposed, or invalidated by a failed or timed-out operation.
    /// A closed attachment never reopens; a caller that needs the host again connects anew.</summary>
    public bool IsClosed => (Volatile.Read(location: ref m_disposed) != 0);

    /// <inheritdoc/>
    Task<ControlResponse> IControlSession.ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(
            request.Operation,
            request.Command,
            request.TimeoutMilliseconds,
            cancellationToken
        );

    /// <summary>Reads a private host capability and proves both peers know its secret. Connects only to IPv4 loopback.</summary>
    /// <param name="attachmentPath">The host's printed attachment file path, never a URL.</param>
    /// <param name="clock">Drives the five-second connect-and-handshake deadline and every call deadline of the
    /// returned attachment; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <param name="cancellationToken">Cancels attachment.</param>
    /// <returns>A new dedicated host session.</returns>
    public static async Task<LocalControlClient> ConnectAsync(string attachmentPath, TimeProvider? clock = null, CancellationToken cancellationToken = default) {
        clock ??= TimeProvider.System;
        var descriptor = LocalEndpointCapability.ReadDescriptor(path: attachmentPath);
        var socket = new TcpClient(family: AddressFamily.InterNetwork) { NoDelay = true };

        try {
            using var deadline = new OperationDeadline(
                caller: cancellationToken,
                timeProvider: clock,
                timeout: ConnectDeadline
            );

            await socket.ConnectAsync(
                IPAddress.Loopback,
                descriptor.Port,
                deadline.Token
            ).ConfigureAwait(continueOnCapturedContext: false);
            await descriptor.AuthenticateAsync(
                socket.GetStream(),
                server: false,
                deadline.Token
            ).ConfigureAwait(continueOnCapturedContext: false);
            return new(
                clock: clock,
                socket: socket
            );
        } catch { socket.Dispose(); throw; }
    }
    /// <summary>Closes this idle attachment when its host has hung up, which the host does on exit or
    /// <c>world.control stop</c>. An idle attachment has no response pending, so a readable socket with no data is
    /// the host's orderly close; nothing dispatched is lost.</summary>
    /// <returns><see langword="true"/> when the attachment is closed.</returns>
    public bool CloseIfHostGone() {
        if (IsClosed) {
            return true;
        }

        if (Volatile.Read(location: ref m_busy) != 0) {
            return false;
        }

        try {
            var socket = m_socket.Client;

            if (!socket.Poll(microSeconds: 0, mode: SelectMode.SelectRead) || (socket.Available != 0)) {
                return false;
            }
        } catch (Exception error) when ((error is SocketException or ObjectDisposedException)) { }

        Dispose();

        return true;
    }
    /// <summary>Closes this attachment and its pending ingress. Does not stop the host.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) == 0) { m_socket.Dispose(); }
    }
    /// <summary>Runs one operation. Concurrent calls refuse immediately; cancellation closes the entire ordered session.</summary>
    /// <param name="operation">exec or capture.</param>
    /// <param name="command">One console line, or null for capture.</param>
    /// <param name="timeoutMilliseconds">Deadline from 1..120000 milliseconds, on the attachment's clock.</param>
    /// <param name="cancellationToken">Cancels queued work; dispatched effects cannot be undone.</param>
    /// <returns>The correlated console result or completed PNG.</returns>
    public async Task<ControlResponse> ExecuteAsync(string operation, string? command, int timeoutMilliseconds = 30_000, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(
            condition: (Volatile.Read(location: ref m_disposed) != 0),
            instance: this
        );
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_busy,
            value: 1
        ) != 0) {
            return new(
            0,
            "refused",
            "Attachment busy; await the active operation.",
            true
        );
        }
        try {
            var request = new ControlRequest(
                Command: command,
                Id: checked((m_sequence + 1)),
                Operation: operation,
                TimeoutMilliseconds: timeoutMilliseconds
            );

            if (LocalControlServer.Validate(request: request) is { } refusal) { return refusal with { Id = 0 }; }
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                request,
                ControlJson.Default.ControlRequest
            );

            if (payload.Length > ControlLimits.RequestBytes) {
                return new(
                0,
                "refused",
                "Encoded request exceeds 16384 bytes.",
                true
            );
            }
            using var deadline = new OperationDeadline(
                caller: cancellationToken,
                timeout: TimeSpan.FromMilliseconds(value: timeoutMilliseconds),
                timeProvider: m_clock
            );

            try {
                m_sequence = request.Id;
                await ControlWire.WriteAsync(
                    m_stream,
                    ControlWire.RequestKind,
                    payload,
                    ControlLimits.RequestBytes,
                    deadline.Token
                ).ConfigureAwait(continueOnCapturedContext: false);
                var bytes = (await ControlWire.ReadAsync(
                    m_stream,
                    ControlWire.ResponseKind,
                    ControlLimits.ResponseBytes,
                    deadline.Token
                ).ConfigureAwait(continueOnCapturedContext: false) ?? throw new EndOfStreamException(message: "Host disconnected; dispatched outcome is unknown."));
                var response = (JsonSerializer.Deserialize(
                    bytes.Span,
                    ControlJson.Default.ControlResponse
                ) ?? throw new InvalidDataException(message: "Missing response."));

                if (!response.IsValidFor(request)) { throw new InvalidDataException(message: "Invalid or mismatched control response."); }
                return response;
            } catch { Dispose(); throw; }
        } finally {
            Volatile.Write(
            location: ref m_busy,
            value: 0
        );
        }
    }
}
