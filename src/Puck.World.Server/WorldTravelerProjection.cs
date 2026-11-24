using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Resolves and streams one authenticated traveler view, never holding an authority gate across network I/O.</summary>
internal static class WorldTravelerProjection {
    public static async Task<string?> StreamAsync(WorldServer server, WorldTravelerObservation request, string endpoint, Stream output, CancellationToken ct, Func<bool>? credentialIsCurrent = null) {
        if (request.RemainingHops is 0 or > 64) { return "traveler projection exceeded its forwarding hop limit"; }
        WorldFederationProjectionSink? sink = null;
        IDisposable? lease = null;
        var refusal = await server.RunOnTickThreadAsync(work: () => {
            if (credentialIsCurrent?.Invoke() == false) { return "the session credential expired or was revoked"; }
            if (!server.TryTransferredPrincipal(
                request.SourceAuthority,
                request.Mobility,
                out var principal
            )) {
                return "the projection credential names no committed traveler";
            }
            if (WorldAdmissionDoor.TryAdmitArrival(
                entries: server.AdmissionPolicy,
                sourceAuthority: request.SourceAuthority,
                verdict: out var admission
            ) is not null) {
                return "the source authority is no longer admitted for traveler projection";
            }
            request = request with {
                Ceiling = ((WorldDisclosureTier)Math.Min(
                val1: ((byte)request.Ceiling),
                val2: ((byte)admission!.Tier)
            )),
            };
            if (request.Ceiling == WorldDisclosureTier.Frames) { return "the projection disclosure tier permits no world document"; }
            if (!WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                principal: principal,
                server: server
            )) { return null; }
            var checkedDefinition = server.Definition;
            bool SameProjection() {
                if (ReferenceEquals(checkedDefinition, server.Definition)) { return true; }
                checkedDefinition = server.Definition;
                return sink?.HoldsProjection != false;
            }
            var subject = GrantSubject.Body(index: principal.Index);

            if (!server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: subject
            ).IsAllowed) {
                return "the traveler holds no Observe grant for its body";
            }
            bool Authorized() => (
                credentialIsCurrent?.Invoke() != false &&
                WorldAdmissionDoor.TryAdmitArrival(server.AdmissionPolicy, request.SourceAuthority, out var liveAdmission) is null &&
                liveAdmission!.Tier >= request.Ceiling &&
                server.TryTransferredPrincipal(
                request.SourceAuthority,
                request.Mobility,
                out var current
            ) && (current == principal) &&
                WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                principal: principal,
                server: server
            ) &&
                server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: subject
            ).IsAllowed);
            bool Current() => SameProjection() && Authorized();
            var view = server.DeliveryView(principal, request.Ceiling, Authorized);

            // The live body policy at every delivery, as every other projection reads it: a policy edit reaches the next
            // delivery, and one that turns redacting cuts this stream.
            sink = new(
                authority: server.AuthorityIdentity,
                disclosure: () => view.Bodies,
                isCurrent: Current,
                recipient: principal,
                server: server,
                tier: request.Ceiling,
                view: view
            );
            sink.PrimeRoute(route: WorldLocalForwardedAuthority.DescribeRoute(
                endpoint: endpoint,
                principal: principal,
                server: server
            ));
            lease = server.AttachSink(sink: sink);
            return ((string?)null);
        }, ct).ConfigureAwait(false);

        if (refusal is not null) { return refusal; }
        if (
            (sink is null) ||
            (lease is null)
        ) {
            if (server.TransferForwarder is not { } forwarder) {
                return "the traveler has no committed onward projection route";
            }

            using var forwardLifetime = CancellationTokenSource.CreateLinkedTokenSource(token: ct);
            // The live policy at this instant, whatever replaced the admission section. The guarded output asks it
            // before every frame, so the cut never depends on an AdmissionChanged notification having arrived.
            bool StillAdmitted() => (
                (WorldAdmissionDoor.TryAdmitArrival(server.AdmissionPolicy, request.SourceAuthority, out var liveAdmission) is null) &&
                (liveAdmission!.Tier >= request.Ceiling)
            );
            var guardedOutput = new PolicyGuardedProjectionOutput(inner: output, outerToken: ct, forwardToken: forwardLifetime.Token, stillAdmitted: StillAdmitted);
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
                            output: guardedOutput,
                            request: request,
                            source: server
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    } catch (OperationCanceledException) when (!ct.IsCancellationRequested && guardedOutput.IsInvalidated) {
                        // A live policy downgrade or removal cuts the forwarded stream cleanly at a frame boundary.
                    }
                }
            } finally {
                server.AdmissionChanged -= CheckForwardPolicy;
            }

            await WorldFederationCodec.WriteResponseAsync(
                body: Encoding.UTF8.GetBytes(s: WorldFederationProjectionSink.InvalidatedDetachReason),
                ct: ct,
                kind: WorldFederationResponse.ProjectionInvalidated,
                stream: output
            ).ConfigureAwait(continueOnCapturedContext: false);
            return null;
        }
        try {
            await sink.StreamAsync(
            ct: ct,
            output: output
        ).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            _ = server.RunOnTickThreadAsync(work: () => {
                lease.Dispose();
                sink.Release();
                return true;
            });
        }
        return null;
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
