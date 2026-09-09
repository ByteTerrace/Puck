using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Puck.Networking;

/// <summary>One response returned by a persistent request lane's protocol — a decoded kind and body, or the named
/// transport failure that stands in when nothing decoded.</summary>
/// <param name="Kind">The response kind. Meaningless when <see cref="Ok"/> is <see langword="false"/>.</param>
/// <param name="Body">The response body. Empty when <see cref="Ok"/> is <see langword="false"/>. The memory is exactly
/// what the protocol's <see cref="ILaneProtocol{TRequestKind,TResponseKind}.ReadResponseAsync"/> returned, and that
/// contract forbids a buffer the protocol reuses, so a caller may keep it for as long as it likes without copying; the
/// dialects in this repository ride <see cref="WireFrame.ReadAsync"/>, which allocates one fresh buffer per frame.</param>
/// <param name="Failure">The named transport refusal when nothing decoded.</param>
public readonly record struct LaneResponse<TResponseKind>(TResponseKind Kind, ReadOnlyMemory<byte> Body, WireFailure Failure)
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
            Body: ReadOnlyMemory<byte>.Empty,
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
/// request/response frames, plus the one fact about each request kind the lane cannot know for itself — whether
/// sending it twice is safe.</summary>
public interface ILaneProtocol<TRequestKind, TResponseKind>
    where TRequestKind : struct, Enum
    where TResponseKind : struct, Enum {
    /// <summary>Writes the dialect's opening Hello.</summary>
    Task WriteHelloAsync(Stream stream, CancellationToken ct);
    /// <summary>Runs the challenge/proof exchange, throwing on refusal.</summary>
    /// <exception cref="IOException">The peer's challenge or verdict is malformed or refused.</exception>
    Task AuthenticateAsync(Stream stream, string sourceAuthority, CancellationToken ct);
    /// <summary>Decides whether a request of the given kind may be sent a second time after the first send's outcome
    /// was lost — the connection broke, or answered with something that did not decode — without the peer being able
    /// to tell the two apart. The lane carries no correlation id, so a re-send of a kind the peer applies as-is
    /// (a submission, say) is a duplicate application; a kind keyed and made idempotent at the peer is safe.</summary>
    /// <param name="kind">The request kind.</param>
    /// <returns><see langword="true"/> when the lane may re-send a request of this kind once; <see langword="false"/>
    /// when it must instead answer <see cref="WireRefusal.ConnectionClosed"/> and leave the request in doubt.</returns>
    bool MayResend(TRequestKind kind);
    /// <summary>Writes one framed request.</summary>
    Task WriteRequestAsync(Stream stream, TRequestKind kind, ReadOnlyMemory<byte> body, CancellationToken ct);
    /// <summary>Reads one framed response. The lane hands the returned <see cref="LaneResponse{TResponseKind}.Body"/>
    /// to the caller unchanged, and the caller may keep it, so it must never alias a buffer the protocol reuses or
    /// returns to a pool — a dialect built on <see cref="WireFrame.ReadAsync"/> satisfies this for free, since that
    /// allocates one fresh buffer per frame.</summary>
    Task<LaneResponse<TResponseKind>> ReadResponseAsync(Stream stream, CancellationToken ct);
}
/// <summary>One authenticated, persistent connection to one peer endpoint, carrying strictly ordered
/// request-then-response traffic. Hello and authentication are paid once for the lane's lifetime; requests then
/// queue behind whatever is already in flight, which is what lets the peer answer without a correlation id on the
/// wire.</summary>
/// <remarks>
/// <para>Only a failure to connect may take the lane out of service, and only after one retry — a listener whose
/// backlog was momentarily full is not an absent peer. A break on an already-established connection, or a response
/// that does not decode, reconnects and re-sends once without entering backoff — that is evidence about one socket,
/// never about the peer — but ONLY when <see cref="ILaneProtocol{TRequestKind,TResponseKind}.MayResend"/> says the
/// kind is safe to send twice; otherwise the request is answered <see cref="WireRefusal.ConnectionClosed"/> with a
/// detail saying it may or may not have been applied, and the caller reconciles. A queued request that later
/// succeeds clears the backoff window outright, so a lane that demonstrably recovered reports
/// <see cref="IsAvailable"/> again at once.</para>
/// <para>Every attempt runs under one per-request deadline (the constructor's <c>requestTimeout</c>) that covers
/// connecting, Hello, authentication, the request write, and the response read together. A deadline that expires
/// before the request write began — during connect, Hello, or authentication — counts as a connect failure and takes
/// the ordinary two-strike path to <see cref="WireRefusal.LaneUnavailable"/>; one that expires once the write began
/// answers <see cref="WireRefusal.RequestTimedOut"/>, drops the connection, never re-sends, and never enters backoff —
/// a silent peer is neither an absent one nor a reason to apply the request twice. The detail says whether the write
/// itself completed: a peer that stalls the write (a full receive window) cannot decode a partial frame, but a write
/// cancelled at its last byte may still have landed whole, so that request too is left in doubt rather than re-sent.
/// The deadline is the lane's only read bound; a caller that wants to wait less applies its own wait to the task
/// <see cref="Enqueue"/> returns.</para>
/// <para>The worker survives everything the protocol can throw: an exception outside the wire vocabulary answers the
/// current request <see cref="WireRefusal.LaneUnavailable"/> naming the exception, drops the connection, and serves
/// the next request. Requests still queued when the worker stops are answered <see cref="WireRefusal.LaneUnavailable"/>
/// too, from a <c>finally</c> that calls no caller code, drops the socket, and closes the queue behind the worker, so
/// cancelling the lifetime token — with or without <see cref="Dispose"/> — releases the connection to the peer rather
/// than holding it open until the finalizer runs, and a request queued afterwards is answered
/// <see cref="WireRefusal.LaneUnavailable"/> at once rather than parked in a channel nobody reads.</para>
/// <para>This class does not itself gate a request on <see cref="IsAvailable"/> — a caller checks it before
/// enqueueing, so a lane inside its unreachable backoff never even reaches a socket attempt for that request.</para>
/// </remarks>
public sealed class PersistentRequestLane<TRequestKind, TResponseKind> : IDisposable
    where TRequestKind : struct, Enum
    where TResponseKind : struct, Enum {
    private readonly record struct PendingRequest(TRequestKind Kind, byte[] Body, TaskCompletionSource<LaneResponse<TResponseKind>> Completion);

    private readonly TimeSpan m_connectRetryDelay;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> m_connect;
    private readonly CancellationTokenSource m_lifetime;
    private readonly Action<Exception>? m_onUnavailable;
    private readonly ILaneProtocol<TRequestKind, TResponseKind> m_protocol;
    private readonly TimeSpan m_requestTimeout;
    private readonly Func<LaneRoute> m_route;
    private readonly string m_sourceAuthority;
    private readonly TimeSpan m_unavailableBackoff;
    private readonly Task m_worker;

    private int m_disposed;
    private Stream? m_stream;
    private int m_unavailableNoted;
    private long m_unavailableUntil;

    private readonly Channel<PendingRequest> m_queue = Channel.CreateUnbounded<PendingRequest>(options: new UnboundedChannelOptions { SingleReader = true });
    private string m_connectedEndpoint = string.Empty;
    // The description of the route most recently sampled by the worker, kept so the worker's own last words (the
    // catch-all and the abandoned-request drain) never have to call back into the caller's route delegate.
    private string m_routeDescription = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/> class
    /// and starts its background worker.</summary>
    /// <param name="route">Reads the current endpoint and its textual identity as one atomic snapshot — a caller
    /// whose route can be republished mid-lifetime reads it fresh on every (re)connect, and the endpoint dialed and
    /// the description recorded for it always come from the same call.</param>
    /// <param name="sourceAuthority">The authority namespace this lane authenticates as.</param>
    /// <param name="protocol">The wire dialect.</param>
    /// <param name="connect">Opens an owned stream through the application's peer transport. The lane never
    /// selects a socket transport or creates a separate identity.</param>
    /// <param name="lifetime">Cancelled when the lane and its worker must stop.</param>
    /// <param name="connectRetryDelay">How long the worker waits before retrying a failed connect. Must lie in
    /// [0, 1 day].</param>
    /// <param name="unavailableBackoff">How long <see cref="IsAvailable"/> reports <see langword="false"/> after a
    /// connect exhausts its retry. Clamped to [0, 1 day].</param>
    /// <param name="requestTimeout">The per-attempt deadline covering connect, Hello, authentication, and the request
    /// write plus response read. Expiry before the request write began is a connect failure; expiry once the write
    /// began answers <see cref="WireRefusal.RequestTimedOut"/>, whether or not the write completed. Must lie in
    /// [0, 1 day] — a value outside that range is refused here rather than failing every request the lane ever serves
    /// and making <see cref="Dispose"/> throw on its bounded join.</param>
    /// <param name="onUnavailable">Invoked once per unavailability episode (never per refused request) with the
    /// exception that took the lane down. It runs on the thread pool, never on the worker, so a callback that
    /// disposes the lane cannot deadlock against it; a throwing callback is contained and never strands the current
    /// request or the worker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> or <paramref name="protocol"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceAuthority"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="connectRetryDelay"/> or
    /// <paramref name="requestTimeout"/> is negative or exceeds one day.</exception>
    public PersistentRequestLane(Func<LaneRoute> route, string sourceAuthority, ILaneProtocol<TRequestKind, TResponseKind> protocol, Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect, CancellationToken lifetime, TimeSpan connectRetryDelay, TimeSpan unavailableBackoff, TimeSpan requestTimeout, Action<Exception>? onUnavailable = null) {
        ArgumentNullException.ThrowIfNull(argument: route);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceAuthority);
        ArgumentNullException.ThrowIfNull(argument: protocol);
        ArgumentNullException.ThrowIfNull(connect);
        m_connect = connect;
        // One day is the same ceiling the backoff is clamped to, and it sits well inside every timer these values
        // reach — the per-attempt CancelAfter, the retry Task.Delay, and the disposal join's Task.Wait — so no value
        // admitted here can make one of them throw later.
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: TimeSpan.Zero,
            value: connectRetryDelay
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: TimeSpan.FromDays(value: 1),
            value: connectRetryDelay
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: TimeSpan.Zero,
            value: requestTimeout
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: TimeSpan.FromDays(value: 1),
            value: requestTimeout
        );

        m_route = route;
        m_sourceAuthority = sourceAuthority;
        m_protocol = protocol;
        m_connectRetryDelay = connectRetryDelay;
        m_unavailableBackoff = unavailableBackoff;
        m_requestTimeout = requestTimeout;
        m_onUnavailable = onUnavailable;
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: lifetime);
        m_worker = Task.Run(function: () => RunAsync(ct: m_lifetime.Token));
    }

    /// <summary>Gets a value indicating whether the lane is outside its unreachable-peer backoff window.</summary>
    public bool IsAvailable => (Environment.TickCount64 >= Interlocked.Read(location: ref m_unavailableUntil));

    private LaneResponse<TResponseKind> Closed() =>
        LaneResponse<TResponseKind>.Refused(
            detail: ((m_routeDescription.Length == 0)
                ? "the lane closed before the request was sent"
                : $"the lane to '{m_routeDescription}' closed before the request was sent"
            ),
            refusal: WireRefusal.LaneUnavailable
        );
    private void Drop() {
        // Swapped out before being disposed so a Dispose racing the worker (Dispose drops the socket BEFORE joining
        // the worker, to unblock a pending read) never disposes one object twice or leaves a disposed one in place.
        var stream = Interlocked.Exchange(
            location1: ref m_stream,
            value: null
        );
        stream?.Dispose();
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

        var stream = await m_connect(route.Endpoint, ct).ConfigureAwait(false);

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
    private static LaneResponse<TResponseKind> InDoubt(string reason, PendingRequest request, LaneRoute route) =>
        LaneResponse<TResponseKind>.Refused(
            detail: $"the lane to '{route.Description}' broke the {request.Kind} exchange after the request was sent and {request.Kind} may not be sent twice, so it may or may not have been applied — {reason}",
            refusal: WireRefusal.ConnectionClosed
        );
    private async Task RunAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                PendingRequest request;

                try {
                    request = await m_queue.Reader.ReadAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
                } catch (Exception exception) when ((exception is OperationCanceledException or ChannelClosedException)) {
                    break;
                }

                LaneResponse<TResponseKind> answer;

                try {
                    answer = await ServeAsync(
                        ct: ct,
                        request: request
                    ).ConfigureAwait(continueOnCapturedContext: false);
                } catch (Exception exception) {
                    // Anything outside the wire vocabulary — a protocol or route delegate that threw — is this
                    // request's answer, never the worker's death: the connection it happened on is not trusted
                    // again, and the next request gets a fresh one.
                    Drop();

                    answer = LaneResponse<TResponseKind>.Refused(
                        detail: $"the lane to '{m_routeDescription}' failed the {request.Kind} exchange — {exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}",
                        refusal: WireRefusal.LaneUnavailable
                    );
                }

                _ = request.Completion.TrySetResult(result: answer);
            }
        } finally {
            // The socket goes with the worker, not only with Dispose: a lifetime cancelled from outside would
            // otherwise leave the connection open until the finalizer ran, with the peer's serving task parked on a
            // read nothing will ever satisfy. Drop is interlocked, so a Dispose racing this exit disposes nothing twice.
            Drop();

            // The door closes on the worker's own exit, not only on Dispose: a lifetime cancelled from outside would
            // otherwise leave the channel open with nobody reading it, and every later Enqueue would park forever.
            // Closing BEFORE draining means the drain sees every write that got through, and Enqueue refuses the rest.
            _ = m_queue.Writer.TryComplete();

            while (m_queue.Reader.TryRead(item: out var abandoned)) {
                _ = abandoned.Completion.TrySetResult(result: Closed());
            }
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
            m_routeDescription = route.Description;

            // One deadline per attempt, over connect + Hello + authenticate + write + read together. Every decision
            // about whether the LANE is closing tests the lifetime token (ct), never this one.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: ct);

            deadline.CancelAfter(delay: m_requestTimeout);

            try {
                await EnsureConnectedAsync(
                    ct: deadline.Token,
                    route: route
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when ((exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)) {
                Drop();

                if (ct.IsCancellationRequested) {
                    break;
                }

                if (deadline.IsCancellationRequested) {
                    exception = new TimeoutException(
                        innerException: exception,
                        message: $"connect and authentication did not complete inside {m_requestTimeout}"
                    );
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

            // Whether the request write ran to completion, so a deadline that expires inside the write itself (a
            // peer whose receive window is full) is narrated as exactly that rather than as a request the peer holds.
            var written = false;

            try {
                var stream = m_stream!;

                await m_protocol.WriteRequestAsync(
                    stream: stream,
                    kind: request.Kind,
                    body: request.Body,
                    ct: deadline.Token
                ).ConfigureAwait(continueOnCapturedContext: false);

                written = true;

                var response = await m_protocol.ReadResponseAsync(
                    ct: deadline.Token,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!response.Ok) {
                    Drop();

                    if (ct.IsCancellationRequested) {
                        break;
                    }

                    if (deadline.IsCancellationRequested) {
                        return TimedOut(
                            request: request,
                            route: route,
                            written: written
                        );
                    }

                    if (hadConnection) {
                        if (m_protocol.MayResend(kind: request.Kind)) {
                            continue;
                        }

                        return InDoubt(
                            reason: response.Failure.ToString(),
                            request: request,
                            route: route
                        );
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
                _ = Interlocked.Exchange(
                    location1: ref m_unavailableUntil,
                    value: 0
                );

                return response;
            } catch (Exception exception) when ((exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)) {
                Drop();

                if (ct.IsCancellationRequested) {
                    break;
                }

                if (deadline.IsCancellationRequested) {
                    return TimedOut(
                        request: request,
                        route: route,
                        written: written
                    );
                }

                var reason = $"{exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                if (!hadConnection) {
                    // The break happened on a connection this lane had just opened, so the peer is reachable but is
                    // not completing the exchange. Report it; do not take the lane out of service for it.
                    return LaneResponse<TResponseKind>.Refused(
                        refusal: WireRefusal.ConnectionClosed,
                        detail: $"the lane to '{route.Description}' broke the {request.Kind} exchange — {reason}"
                    );
                }

                if (!m_protocol.MayResend(kind: request.Kind)) {
                    return InDoubt(
                        reason: reason,
                        request: request,
                        route: route
                    );
                }
            }
        }

        return LaneResponse<TResponseKind>.Refused(
            detail: $"the lane to '{route.Description}' is closed",
            refusal: WireRefusal.LaneUnavailable
        );
    }
    private LaneResponse<TResponseKind> TimedOut(PendingRequest request, LaneRoute route, bool written) =>
        LaneResponse<TResponseKind>.Refused(
            detail: (written
                ? $"'{route.Description}' did not answer {request.Kind} inside {m_requestTimeout}; the request was written and is not re-sent, so it may or may not have been applied"
                : $"'{route.Description}' did not take {request.Kind} inside {m_requestTimeout}; the request write did not complete and is not re-sent, so it may or may not have been applied"
            ),
            refusal: WireRefusal.RequestTimedOut
        );
    private LaneResponse<TResponseKind> Unreachable(Exception exception, LaneRoute route) {
        _ = Interlocked.Exchange(
            location1: ref m_unavailableUntil,
            value: (Environment.TickCount64 + ((long)Math.Clamp(
                max: TimeSpan.FromDays(value: 1).TotalMilliseconds,
                min: 0,
                value: m_unavailableBackoff.TotalMilliseconds
            )))
        );

        if ((Interlocked.Exchange(
            location1: ref m_unavailableNoted,
            value: 1
        ) == 0) && (m_onUnavailable is { } onUnavailable)) {
            // Off the worker, so a callback that disposes the lane (and so joins the worker) cannot deadlock against
            // the very task it runs on; contained, so its own failure never strands a request or takes the worker
            // down.
            _ = Task.Run(action: () => {
                try {
                    onUnavailable(obj: exception);
                } catch (Exception callbackException) {
                    Console.Error.WriteLine(value: $"[persistent-request-lane onUnavailable callback threw — {callbackException.GetType().Name}: {callbackException.Message.ReplaceLineEndings(replacementText: " ")}]");
                }
            });
        }

        return LaneResponse<TResponseKind>.Refused(
            refusal: WireRefusal.LaneUnavailable,
            detail: $"the peer at '{route.Description}' is unreachable — {exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}"
        );
    }

    /// <summary>Stops the worker and releases the socket. Idempotent and never throws: the lifetime is cancelled, the
    /// queue is closed, the socket is dropped FIRST (so a worker parked in a read unblocks), then the worker is joined
    /// for at most <c>requestTimeout</c> plus one second — a join that outlasts that bound is abandoned, not
    /// surfaced.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) {
            return;
        }

        m_lifetime.Cancel();
        _ = m_queue.Writer.TryComplete();
        Drop();

        try {
            _ = m_worker.Wait(timeout: (m_requestTimeout + TimeSpan.FromSeconds(value: 1)));
        } catch (AggregateException) {
            // The lane is being torn down; whatever its worker's last error was is not a caller's answer.
        }

        Drop();
        m_lifetime.Dispose();
    }
    /// <summary>Queues one request and returns its eventual answer. Never waits on a socket itself — the caller
    /// decides how long to wait, if at all; the lane's own <c>requestTimeout</c> bounds each attempt, so the answer
    /// always arrives eventually. A request queued after <see cref="Dispose"/>, or after the lifetime token cancelled
    /// and the worker stopped, is answered <see cref="WireRefusal.LaneUnavailable"/> at once.</summary>
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
