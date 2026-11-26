using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The one door that opens a segment of a traveler's observation stream: it resolves an authenticated
/// credential to where the traveler stands, serves that authority's view of the world to the stream, and never holds
/// an authority gate across network I/O.</summary>
/// <remarks><para>A recipient's stream outlives its traveler's stay at any one authority. The authority that holds the
/// recipient's connection (<see cref="StreamAsync"/>) keeps the stream open: when the traveler moves on, the segment
/// that served it ends as moved, and the door resolves the credential again and opens the next segment, served here or
/// relayed from the authority the traveler now stands at. Every segment stamps from the session's one lineage
/// (<see cref="WorldStreamLineage"/>), so the handoff reaches the recipient as a cut of the stream it already holds and
/// never as the stream's end.</para>
/// <para>Invariant: the segments of one stream run one after another, never two at once. What enforces it: only this
/// door opens a segment, a segment is opened only after the one before it has returned, and a segment returns only
/// after its last record is written. Where it stops: an authority in another process that serves a relayed segment
/// (<see cref="WorldTravelerObservation.Continuation"/>) is trusted to stop writing before it hands the stream back;
/// the relaying authority checks each relayed stamp against the lineage and ends the relay on one out of
/// order.</para></remarks>
internal static class WorldTravelerProjection {
    private const string HopLimitRefusal = "traveler projection exceeded its forwarding hop limit";

    // Where resolving a credential at one authority left its stream: an outcome with nothing to serve, a sink attached
    // here, or neither when the traveler stands further along this authority's route.
    private readonly record struct Place(WorldProjectionOutcome? Outcome, WorldTravelerObservation Request, WorldFederationProjectionSink? Sink = null, IDisposable? Lease = null);

    /// <summary>Serves a traveler's observation stream to the connection that asked for it, for as long as the
    /// connection, the credential and this authority's admission of its source stand.</summary>
    /// <param name="server">The authority the connection reached.</param>
    /// <param name="request">The credential and its bounds. One that continues another authority's stream
    /// (<see cref="WorldTravelerObservation.Continuation"/>) is served as one segment and handed back when the
    /// traveler moves on; any other is kept open across the traveler's moves.</param>
    /// <param name="endpoint">The endpoint text a route description reports for <paramref name="server"/>.</param>
    /// <param name="output">The connection, which the caller owns.</param>
    /// <param name="ct">The connection's lifetime.</param>
    /// <param name="credentialIsCurrent">Reads whether the connection's own credential still stands, or
    /// <see langword="null"/> for one that always does.</param>
    /// <returns>The named refusal when nothing was written, or <see langword="null"/> once the stream has ended.</returns>
    public static async Task<string?> StreamAsync(WorldServer server, WorldTravelerObservation request, string endpoint, Stream output, CancellationToken ct, Func<bool>? credentialIsCurrent = null) {
        if (request.RemainingHops is 0 or > 64) { return HopLimitRefusal; }
        var handsBack = request.Continuation.HasValue;
        var session = new WorldProjectionSession(
            lineage: ((request.Continuation is { } taken) ? new WorldStreamLineage(last: in taken) : new WorldStreamLineage()),
            output: output
        );
        // The first segment is placed before the connection is read, so a refusal is answered with nothing started.
        var place = await OpenAsync(credentialIsCurrent: credentialIsCurrent, ct: ct, endpoint: endpoint, request: request, server: server, session: session).ConfigureAwait(continueOnCapturedContext: false);

        if (place.Outcome is { End: WorldProjectionEnd.Refused, Reason: var refusal }) {
            return refusal;
        }
        await WorldProjectionStream.RunAsync(
            output,
            async token => {
                while (true) {
                    var outcome = (place.Outcome ?? await ServeAsync(ct: token, place: place, server: server, session: session).ConfigureAwait(continueOnCapturedContext: false));

                    switch (outcome.End) {
                        case WorldProjectionEnd.Ended:
                            return;
                        case WorldProjectionEnd.Refused:
                            await WorldFederationProjectionSink.WriteEndAsync(ct: token, output: output, reason: WorldFederationProjectionSink.InvalidatedDetachReason).ConfigureAwait(continueOnCapturedContext: false);
                            return;
                        case WorldProjectionEnd.Moved when handsBack:
                            await WorldFederationCodec.WriteResponseAsync(body: default, ct: token, kind: WorldFederationResponse.ProjectionMoved, stream: output).ConfigureAwait(continueOnCapturedContext: false);
                            return;
                    }
                    // The traveler moved on, or is between authorities. Each resolution waits for this authority's
                    // next tick boundary, which paces the wait for a crossing in flight on the authority's own clock.
                    place = await OpenAsync(credentialIsCurrent: credentialIsCurrent, ct: token, endpoint: endpoint, request: request, server: server, session: session).ConfigureAwait(continueOnCapturedContext: false);
                }
            },
            session.AcknowledgeAsync,
            ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        return null;
    }
    /// <summary>Serves one segment of a stream another authority of this process holds the connection of: resolves the
    /// credential here once and serves the traveler where it stands, here or further along this authority's route.</summary>
    /// <param name="server">The authority the stream was forwarded to.</param>
    /// <param name="request">The credential this authority's source holds for the traveler, and its bounds.</param>
    /// <param name="endpoint">The endpoint text a route description reports for <paramref name="server"/>.</param>
    /// <param name="session">The stream.</param>
    /// <param name="ct">The stream's lifetime.</param>
    /// <returns>How the segment came out.</returns>
    public static async Task<WorldProjectionOutcome> RelayAsync(WorldServer server, WorldTravelerObservation request, string endpoint, WorldProjectionSession session, CancellationToken ct) {
        if (request.RemainingHops is 0 or > 64) { return WorldProjectionOutcome.Refused(reason: HopLimitRefusal); }
        var place = await OpenAsync(credentialIsCurrent: null, ct: ct, endpoint: endpoint, request: request, server: server, session: session).ConfigureAwait(continueOnCapturedContext: false);

        return (place.Outcome ?? await ServeAsync(ct: ct, place: place, server: server, session: session).ConfigureAwait(continueOnCapturedContext: false));
    }

    // Resolves the credential under the authority gate and, when the traveler stands here, attaches the segment's sink
    // in the same gated operation, so the principal it resolved cannot be detached or replaced before the sink watches.
    private static async Task<Place> OpenAsync(WorldServer server, WorldTravelerObservation request, string endpoint, WorldProjectionSession session, Func<bool>? credentialIsCurrent, CancellationToken ct) =>
        await server.RunOnTickThreadAsync(work: () => {
            static Place Refuse(string reason, WorldTravelerObservation request) => new(Outcome: WorldProjectionOutcome.Refused(reason: reason), Request: request);
            if (credentialIsCurrent?.Invoke() == false) { return Refuse(reason: "the session credential expired or was revoked", request: request); }
            if (!server.TryTransferredPrincipal(
                request.SourceAuthority,
                request.Mobility,
                out var principal
            )) {
                return Refuse(reason: "the projection credential names no committed traveler", request: request);
            }
            if (WorldAdmissionDoor.TryAdmitArrival(
                entries: server.AdmissionPolicy,
                sourceAuthority: request.SourceAuthority,
                verdict: out var admission,
                kind: WorldAdmissionArrival.Visitor
            ) is not null) {
                return Refuse(reason: "the source authority is no longer admitted for traveler projection", request: request);
            }
            request = request with {
                Ceiling = ((WorldDisclosureTier)Math.Min(
                val1: ((byte)request.Ceiling),
                val2: ((byte)admission!.Tier)
            )),
            };
            if (request.Ceiling == WorldDisclosureTier.Frames) { return Refuse(reason: "the projection disclosure tier permits no world document", request: request); }
            if (!WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                principal: principal,
                server: server
            )) {
                // A reservation that holds the traveler here is its way back: the body lands at this authority, so
                // the route this authority kept from the traveler's last departure leads nowhere it will stand.
                return (server.TransferEscrow.AwaitsTraveler(incarnation: request.Mobility.Incarnation)
                    ? new Place(Outcome: WorldProjectionOutcome.InFlight(reason: "the traveler's arrival at this authority is in flight"), Request: request)
                    : new Place(Outcome: null, Request: request));
            }
            var subject = GrantSubject.Body(index: principal.Index);

            if (!server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: subject
            ).IsAllowed) {
                return Refuse(reason: "the traveler holds no Observe grant for its body", request: request);
            }
            var held = request;
            bool Entitled() => (
                credentialIsCurrent?.Invoke() != false &&
                WorldAdmissionDoor.TryAdmitArrival(server.AdmissionPolicy, held.SourceAuthority, out var liveAdmission, WorldAdmissionArrival.Visitor) is null &&
                liveAdmission!.Tier >= held.Ceiling);
            bool Stands() => (
                server.TryTransferredPrincipal(
                held.SourceAuthority,
                held.Mobility,
                out var current
            ) && (current == principal) &&
                WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                principal: principal,
                server: server
            ));
            bool Authorized() => (
                Entitled() &&
                Stands() &&
                server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: subject
            ).IsAllowed);
            // The stream goes on elsewhere when the credential still names a traveler here and that traveler no longer
            // stands here as the principal this segment serves.
            bool HasMoved() => (Entitled() && server.TryTransferredPrincipal(held.SourceAuthority, held.Mobility, out _) && !Stands());
            var view = server.DeliveryView(principal, held.Ceiling, Authorized);

            // The live body policy at every delivery, as every other projection reads it: a policy edit reaches the next
            // delivery, and one that turns redacting cuts this stream.
            var sink = new WorldFederationProjectionSink(
                disclosure: () => view.Bodies,
                hasMoved: HasMoved,
                isCurrent: Authorized,
                lineage: session.Lineage,
                recipient: principal,
                server: server,
                tier: held.Ceiling,
                view: view
            );

            sink.PrimeRoute(route: WorldLocalForwardedAuthority.DescribeRoute(
                endpoint: endpoint,
                principal: principal,
                server: server
            ));
            return new Place(Lease: server.AttachSink(sink: sink), Outcome: null, Request: held, Sink: sink);
        }, ct).ConfigureAwait(false);

    private static Task<WorldProjectionOutcome> ServeAsync(WorldServer server, Place place, WorldProjectionSession session, CancellationToken ct) =>
        (((place.Sink is { } sink) && (place.Lease is { } lease))
            ? ServeHereAsync(ct: ct, lease: lease, server: server, session: session, sink: sink)
            : ServeOnwardAsync(ct: ct, request: place.Request, server: server, session: session));

    private static async Task<WorldProjectionOutcome> ServeHereAsync(WorldServer server, WorldFederationProjectionSink sink, IDisposable lease, WorldProjectionSession session, CancellationToken ct) {
        try {
            using (session.Receive(acknowledge: (stamp, _, _) => {
                sink.Acknowledge(stamp: in stamp);
                return ValueTask.CompletedTask;
            })) {
                await sink.PumpAsync(
                    ct: ct,
                    output: session.Output
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (sink.DetachReason == WorldFederationProjectionSink.MovedDetachReason) {
                return WorldProjectionOutcome.Moved;
            }
            await WorldFederationProjectionSink.WriteEndAsync(ct: ct, output: session.Output, reason: sink.DetachReason).ConfigureAwait(continueOnCapturedContext: false);
            return WorldProjectionOutcome.Ended;
        } finally {
            // Queued behind nothing but earlier work of this connection, so the segment is detached before the next
            // resolution at this authority runs.
            _ = server.RunOnTickThreadAsync(work: () => {
                lease.Dispose();
                sink.Release();
                return true;
            });
        }
    }

    private static async Task<WorldProjectionOutcome> ServeOnwardAsync(WorldServer server, WorldTravelerObservation request, WorldProjectionSession session, CancellationToken ct) {
        if (server.TransferForwarder is not { } forwarder) {
            return WorldProjectionOutcome.Refused(reason: "the traveler has no committed onward projection route");
        }
        // A traveler detaches before its onward route is published, so a credential that still stands with no route
        // names a crossing in flight.
        if (!forwarder.HasOnwardRoute(mobility: request.Mobility, source: server)) {
            return WorldProjectionOutcome.InFlight(reason: "the traveler's onward route is not published yet");
        }

        using var forwardLifetime = CancellationTokenSource.CreateLinkedTokenSource(token: ct);
        // The live policy at this instant, whatever replaced the admission section. The guarded output asks it
        // before every frame, so the cut never depends on an AdmissionChanged notification having arrived.
        bool StillAdmitted() => (
            (WorldAdmissionDoor.TryAdmitArrival(server.AdmissionPolicy, request.SourceAuthority, out var liveAdmission, WorldAdmissionArrival.Visitor) is null) &&
            (liveAdmission!.Tier >= request.Ceiling)
        );
        var guardedOutput = new PolicyGuardedProjectionOutput(inner: session.Output, outerToken: ct, forwardToken: forwardLifetime.Token, stillAdmitted: StillAdmitted);
        // Wakes a relay stalled between frames; the per-frame check is what enforces the policy.
        void CheckForwardPolicy() {
            if (!StillAdmitted()) {
                guardedOutput.Invalidate();
                try { _ = forwardLifetime.CancelAsync(); } catch (ObjectDisposedException) { }
            }
        }

        server.AdmissionChanged += CheckForwardPolicy;
        try {
            var preflightInvalidated = await server.RunOnTickThreadAsync(work: () => {
                CheckForwardPolicy();
                return guardedOutput.IsInvalidated;
            }, ct).ConfigureAwait(false);

            if (!preflightInvalidated) {
                try {
                    return await forwarder.StreamForwardedProjectionAsync(
                        ct: forwardLifetime.Token,
                        request: request,
                        session: session.WithOutput(output: guardedOutput),
                        source: server
                    ).ConfigureAwait(continueOnCapturedContext: false);
                } catch (OperationCanceledException) when (!ct.IsCancellationRequested && guardedOutput.IsInvalidated) {
                    // A live policy downgrade or removal cuts the forwarded stream cleanly at a frame boundary.
                }
            }
        } finally {
            server.AdmissionChanged -= CheckForwardPolicy;
        }

        await WorldFederationProjectionSink.WriteEndAsync(ct: ct, output: session.Output, reason: WorldFederationProjectionSink.InvalidatedDetachReason).ConfigureAwait(continueOnCapturedContext: false);
        return WorldProjectionOutcome.Ended;
    }

    // Completes any already-started downstream frame write on outerToken before observing forwardToken cancellation,
    // so ProjectionInvalidated never lands after a partial frame. A frame's first write asks stillAdmitted, so the
    // relay holds the live policy at every frame boundary.
    private sealed class PolicyGuardedProjectionOutput(Stream inner, CancellationToken outerToken, CancellationToken forwardToken, Func<bool> stillAdmitted) : Stream {
        private int m_invalidated;
        private bool m_midFrame;

        public bool IsInvalidated => (Volatile.Read(location: ref m_invalidated) != 0);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Invalidate() => _ = Interlocked.Exchange(location1: ref m_invalidated, value: 1);
        public override void Flush() => inner.Flush();
        public override async Task FlushAsync(CancellationToken cancellationToken) {
            await inner.FlushAsync(cancellationToken: outerToken).ConfigureAwait(continueOnCapturedContext: false);
            m_midFrame = false;
            if (IsInvalidated) {
                throw new OperationCanceledException(token: forwardToken);
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer: buffer, offset: offset, count: count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer: buffer, cancellationToken: cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer: buffer, offset: offset, count: count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer: buffer.AsMemory(start: offset, length: count), cancellationToken: cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            if (!m_midFrame) {
                if (!IsInvalidated && !stillAdmitted()) { Invalidate(); }
                if (IsInvalidated) { throw new OperationCanceledException(token: forwardToken); }
            }
            m_midFrame = true;
            await inner.WriteAsync(buffer: buffer, cancellationToken: outerToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
