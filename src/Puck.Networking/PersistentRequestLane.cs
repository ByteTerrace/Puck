using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Puck.Networking;

/// <summary>One response returned by a persistent request lane's protocol — a decoded kind and body, or the named
/// transport failure that stands in when nothing decoded.</summary>
/// <param name="Kind">The response kind. Meaningless when <see cref="Ok"/> is <see langword="false"/>.</param>
/// <param name="Body">The response body. Empty when <see cref="Ok"/> is <see langword="false"/>.</param>
/// <param name="Failure">The named transport refusal when nothing decoded.</param>
public readonly record struct LaneResponse<TResponseKind>(TResponseKind Kind, byte[] Body, WireFailure Failure)
    where TResponseKind : struct, Enum {
    /// <summary>Gets a value indicating whether the response decoded (no refusal).</summary>
    public bool Ok => !Failure.IsRefusal;

    /// <summary>Creates a refused response.</summary>
    /// <param name="refusal">The refusal name.</param>
    /// <param name="detail">The refusal detail.</param>
    /// <returns>The refused response.</returns>
    public static LaneResponse<TResponseKind> Refused(WireRefusal refusal, string detail) =>
        new(
            Kind: default,
            Body: [],
            Failure: new WireFailure(
                Detail: detail,
                Refusal: refusal
            )
        );
}
/// <summary>One connection target for a <see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/>: the socket
/// endpoint to dial and its textual identity, sampled together from one atomic snapshot so a route republished
/// mid-connect can never record a socket connected to one endpoint under a different endpoint's description.</summary>
/// <param name="Endpoint">The endpoint to dial.</param>
/// <param name="Description">The endpoint's textual identity, compared to detect a republished route.</param>
public readonly record struct LaneRoute(IPEndPoint Endpoint, string Description);
/// <summary>The wire behavior a <see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/> needs from its
/// concrete dialect: a Hello, a challenge/proof authentication paid once per connection, then strictly ordered
/// request/response frames.</summary>
public interface ILaneProtocol<TRequestKind, TResponseKind>
    where TRequestKind : struct, Enum
    where TResponseKind : struct, Enum {
    /// <summary>Writes the dialect's opening Hello.</summary>
    Task WriteHelloAsync(Stream stream, CancellationToken ct);
    /// <summary>Runs the challenge/proof exchange, throwing on refusal.</summary>
    /// <exception cref="IOException">The peer's challenge or verdict is malformed or refused.</exception>
    Task AuthenticateAsync(Stream stream, string sourceAuthority, CancellationToken ct);
    /// <summary>Writes one framed request.</summary>
    Task WriteRequestAsync(Stream stream, TRequestKind kind, ReadOnlyMemory<byte> body, CancellationToken ct);
    /// <summary>Reads one framed response.</summary>
    Task<LaneResponse<TResponseKind>> ReadResponseAsync(Stream stream, CancellationToken ct);
}
/// <summary>One authenticated, persistent connection to one peer endpoint, carrying strictly ordered
/// request-then-response traffic. Hello and authentication are paid once for the lane's lifetime; requests then
/// queue behind whatever is already in flight, which is what lets the peer answer without a correlation id on the
/// wire.</summary>
/// <remarks>
/// Only a failure to connect may take the lane out of service, and only after one retry — a listener whose backlog
/// was momentarily full is not an absent peer. A break on an already-established connection reconnects and
/// re-sends once, without entering backoff: that is evidence about one socket, never about the peer. This class does
/// not itself gate a request on <see cref="IsAvailable"/> — a caller checks it before enqueueing, so a lane inside
/// its unreachable backoff never even reaches a socket attempt for that request.
/// </remarks>
public sealed class PersistentRequestLane<TRequestKind, TResponseKind> : IDisposable
    where TRequestKind : struct, Enum
    where TResponseKind : struct, Enum {
    private readonly record struct PendingRequest(TRequestKind Kind, byte[] Body, TaskCompletionSource<LaneResponse<TResponseKind>> Completion);

    private readonly TimeSpan m_connectRetryDelay;
    private readonly CancellationTokenSource m_lifetime;
    private readonly Action<Exception>? m_onUnavailable;
    private readonly ILaneProtocol<TRequestKind, TResponseKind> m_protocol;
    private readonly Func<LaneRoute> m_route;
    private readonly string m_sourceAuthority;
    private readonly TimeSpan m_unavailableBackoff;
    private readonly Task m_worker;

    private TcpClient? m_client;
    private Stream? m_stream;
    private int m_unavailableNoted;
    private long m_unavailableUntil;

    private readonly Channel<PendingRequest> m_queue = Channel.CreateUnbounded<PendingRequest>(options: new UnboundedChannelOptions { SingleReader = true });
    private string m_connectedEndpoint = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/> class
    /// and starts its background worker.</summary>
    /// <param name="route">Reads the current endpoint and its textual identity as one atomic snapshot — a caller
    /// whose route can be republished mid-lifetime reads it fresh on every (re)connect, and the endpoint dialed and
    /// the description recorded for it always come from the same call.</param>
    /// <param name="sourceAuthority">The authority namespace this lane authenticates as.</param>
    /// <param name="protocol">The wire dialect.</param>
    /// <param name="lifetime">Cancelled when the lane and its worker must stop.</param>
    /// <param name="connectRetryDelay">How long the worker waits before retrying a failed connect.</param>
    /// <param name="unavailableBackoff">How long <see cref="IsAvailable"/> reports <see langword="false"/> after a
    /// connect exhausts its retry.</param>
    /// <param name="onUnavailable">Invoked once per unavailability episode (never per refused request) with the
    /// exception that took the lane down. A throwing callback is contained — it never strands the current request
    /// or the worker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> or <paramref name="protocol"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceAuthority"/> is null or whitespace.</exception>
    public PersistentRequestLane(Func<LaneRoute> route, string sourceAuthority, ILaneProtocol<TRequestKind, TResponseKind> protocol, CancellationToken lifetime, TimeSpan connectRetryDelay, TimeSpan unavailableBackoff, Action<Exception>? onUnavailable = null) {
        ArgumentNullException.ThrowIfNull(argument: route);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceAuthority);
        ArgumentNullException.ThrowIfNull(argument: protocol);

        m_route = route;
        m_sourceAuthority = sourceAuthority;
        m_protocol = protocol;
        m_connectRetryDelay = connectRetryDelay;
        m_unavailableBackoff = unavailableBackoff;
        m_onUnavailable = onUnavailable;
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: lifetime);
        m_worker = Task.Run(function: () => RunAsync(ct: m_lifetime.Token));
    }

    /// <summary>Gets a value indicating whether the lane is outside its unreachable-peer backoff window.</summary>
    public bool IsAvailable => (Stopwatch.GetTimestamp() >= Interlocked.Read(location: ref m_unavailableUntil));

    private void Drop() {
        m_stream?.Dispose();
        m_client?.Dispose();
        m_stream = null;
        m_client = null;
        m_connectedEndpoint = string.Empty;
    }
    private async Task EnsureConnectedAsync(LaneRoute route, CancellationToken ct) {
        if (
            (m_stream is not null) &&
            string.Equals(
            a: m_connectedEndpoint,
            b: route.Description,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return;
        }

        Drop();

        var client = new TcpClient { NoDelay = true };

        m_client = client;

        await client.ConnectAsync(
            cancellationToken: ct,
            remoteEP: route.Endpoint
        ).ConfigureAwait(continueOnCapturedContext: false);

        var stream = client.GetStream();

        m_stream = stream;
        m_connectedEndpoint = route.Description;

        await m_protocol.WriteHelloAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        await m_protocol.AuthenticateAsync(
            ct: ct,
            sourceAuthority: m_sourceAuthority,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task RunAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            PendingRequest request;

            try {
                request = await m_queue.Reader.ReadAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when ((exception is OperationCanceledException or ChannelClosedException)) {
                break;
            }

            var answer = await ServeAsync(
                ct: ct,
                request: request
            ).ConfigureAwait(continueOnCapturedContext: false);

            _ = request.Completion.TrySetResult(result: answer);
        }

        while (m_queue.Reader.TryRead(item: out var abandoned)) {
            _ = abandoned.Completion.TrySetResult(result: LaneResponse<TResponseKind>.Refused(
                detail: $"the lane to '{m_route().Description}' closed before the request was sent",
                refusal: WireRefusal.LaneUnavailable
            ));
        }
    }
    private async Task<LaneResponse<TResponseKind>> ServeAsync(PendingRequest request, CancellationToken ct) {
        var connectFailures = 0;
        // Reused for every narration this call produces — sampled once per attempt (below), never re-read for a
        // message describing that same attempt, so a route republished after the attempt cannot change what got
        // narrated about it.
        var route = default(LaneRoute);

        for (var attempt = 0; ((attempt < 3) && !ct.IsCancellationRequested); attempt++) {
            var hadConnection = (m_stream is not null);

            route = m_route();

            try {
                await EnsureConnectedAsync(
                    ct: ct,
                    route: route
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when ((exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)) {
                Drop();

                if (ct.IsCancellationRequested) {
                    break;
                }

                if (++connectFailures >= 2) {
                    return Unreachable(
                        exception: exception,
                        route: route
                    );
                }

                try {
                    await Task.Delay(
                        cancellationToken: ct,
                        delay: m_connectRetryDelay
                    ).ConfigureAwait(continueOnCapturedContext: false);
                } catch (OperationCanceledException) {
                    break;
                }

                continue;
            }

            try {
                var stream = m_stream!;

                await m_protocol.WriteRequestAsync(
                    stream: stream,
                    kind: request.Kind,
                    body: request.Body,
                    ct: ct
                ).ConfigureAwait(continueOnCapturedContext: false);

                var response = await m_protocol.ReadResponseAsync(
                    ct: ct,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!response.Ok) {
                    Drop();

                    if (hadConnection) {
                        continue;
                    }

                    // A connection this lane opened itself answered with something that is not a frame. That is the
                    // peer's answer, not a transport outage, so it is reported without taking the lane down.
                    return LaneResponse<TResponseKind>.Refused(
                        refusal: response.Failure.Refusal,
                        detail: $"'{route.Description}' answered {request.Kind} with {response.Failure}"
                    );
                }

                _ = Interlocked.Exchange(
                    location1: ref m_unavailableNoted,
                    value: 0
                );

                return response;
            } catch (Exception exception) when ((exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)) {
                Drop();

                if (ct.IsCancellationRequested) {
                    break;
                }

                if (!hadConnection) {
                    // The break happened on a connection this lane had just opened, so the peer is reachable but is
                    // not completing the exchange. Report it; do not take the lane out of service for it.
                    return LaneResponse<TResponseKind>.Refused(
                        refusal: WireRefusal.ConnectionClosed,
                        detail: $"the lane to '{route.Description}' broke the {request.Kind} exchange — {exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}"
                    );
                }
            }
        }

        return LaneResponse<TResponseKind>.Refused(
            detail: $"the lane to '{route.Description}' is closed",
            refusal: WireRefusal.LaneUnavailable
        );
    }
    private LaneResponse<TResponseKind> Unreachable(Exception exception, LaneRoute route) {
        _ = Interlocked.Exchange(
            location1: ref m_unavailableUntil,
            value: (Stopwatch.GetTimestamp() + ((long)(m_unavailableBackoff.TotalSeconds * Stopwatch.Frequency)))
        );

        if (Interlocked.Exchange(
            location1: ref m_unavailableNoted,
            value: 1
        ) == 0) {
            try {
                m_onUnavailable?.Invoke(obj: exception);
            } catch (Exception callbackException) {
                // A caller-supplied callback must never strand the request this failure belongs to or take the
                // worker down; its own failure is contained here rather than left to escape into RunAsync.
                Console.Error.WriteLine(value: $"[persistent-request-lane onUnavailable callback threw — {callbackException.GetType().Name}: {callbackException.Message.ReplaceLineEndings(replacementText: " ")}]");
            }
        }

        return LaneResponse<TResponseKind>.Refused(
            refusal: WireRefusal.LaneUnavailable,
            detail: $"the peer at '{route.Description}' is unreachable — {exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}"
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_lifetime.Cancel();
        _ = m_queue.Writer.TryComplete();

        try {
            m_worker.GetAwaiter().GetResult();
        } catch (Exception exception) when ((exception is OperationCanceledException or IOException or SocketException)) {
            // The lane is being torn down; its socket's last error is not a caller's answer.
        }

        Drop();
        m_lifetime.Dispose();
    }
    /// <summary>Queues one request and returns its eventual answer. Never waits on a socket itself — the caller
    /// decides how long to wait, if at all.</summary>
    /// <param name="kind">The request kind.</param>
    /// <param name="body">The encoded request body.</param>
    /// <returns>The eventual response.</returns>
    public Task<LaneResponse<TResponseKind>> Enqueue(TRequestKind kind, byte[] body) {
        var pending = new PendingRequest(
            Kind: kind,
            Body: body,
            Completion: new TaskCompletionSource<LaneResponse<TResponseKind>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously)
        );

        if (!m_queue.Writer.TryWrite(item: pending)) {
            _ = pending.Completion.TrySetResult(result: LaneResponse<TResponseKind>.Refused(
                detail: $"the lane to '{m_route().Description}' is closed",
                refusal: WireRefusal.LaneUnavailable
            ));
        }

        return pending.Completion.Task;
    }
}
