using Puck.Networking;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Adapts <see cref="WorldFederationCodec"/>'s Hello/authenticate/request/response grammar onto
/// <see cref="ILaneProtocol{TRequestKind,TResponseKind}"/>, so <see cref="WorldRemoteAuthority"/>'s per-authority
/// lanes ride the generic <see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/> state machine.</summary>
/// <param name="owner">The authority whose <see cref="WorldRemoteAuthority.AuthenticateAsync"/> proves this lane's identity.</param>
internal sealed class WorldFederationLaneProtocol(WorldRemoteAuthority owner) : ILaneProtocol<WorldFederationRequest, WorldFederationResponse> {
    // ILaneProtocol's own sourceAuthority parameter is the generic lane's bookkeeping label, never a claim this
    // dialect's authentication asserts — WorldRemoteAuthority.AuthenticateAsync proves its own configured identity
    // and asserts no namespace alongside the proof (see IAuthenticator's own remarks), so it is deliberately
    // discarded here rather than threaded through.
    /// <inheritdoc/>
    public Task AuthenticateAsync(Stream stream, string sourceAuthority, CancellationToken ct) => owner.AuthenticateAsync(
        ct: ct,
        stream: stream
    );
    /// <inheritdoc/>
    /// <remarks>Every kind other than <see cref="WorldFederationRequest.Submission"/> answers <see langword="true"/>.
    /// Of the kinds a lane actually carries, <see cref="WorldFederationRequest.Reserve"/>,
    /// <see cref="WorldFederationRequest.Commit"/>, <see cref="WorldFederationRequest.Abort"/>,
    /// <see cref="WorldFederationRequest.AcknowledgeTransfer"/>, and <see cref="WorldFederationRequest.Status"/> are
    /// keyed by transfer id and <see cref="WorldFederationRequest.Route"/> is read-only, all idempotent at the host, so
    /// a second copy of one is harmless; the remaining kinds (observe, intent, and the intent stream's own frames) ride
    /// their own sessions and never reach a lane. A Submission is decoded and applied straight through with no dedup,
    /// so a re-send would apply it twice; the lane answers <see cref="WireRefusal.ConnectionClosed"/> instead and the
    /// caller treats the submission as in doubt.</remarks>
    public bool MayResend(WorldFederationRequest kind) => (kind is not WorldFederationRequest.Submission);
    /// <inheritdoc/>
    public async Task<LaneResponse<WorldFederationResponse>> ReadResponseAsync(Stream stream, CancellationToken ct) {
        var read = await WorldFederationCodec.ReadResponseAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (read.Ok
            ? new LaneResponse<WorldFederationResponse>(
                Kind: ((WorldFederationResponse)read.Kind),
                Body: read.Body,
                Failure: default
            )
            : LaneResponse<WorldFederationResponse>.Refused(
                refusal: read.Failure.Refusal,
                detail: read.Failure.Detail
            )
        );
    }
    /// <inheritdoc/>
    public Task WriteHelloAsync(Stream stream, CancellationToken ct) => HandshakeWireFormat.WriteHelloAsync(
        ct: ct,
        key: WorldFederationCodec.WireKey,
        stream: stream
    );
    /// <inheritdoc/>
    public Task WriteRequestAsync(Stream stream, WorldFederationRequest kind, ReadOnlyMemory<byte> body, CancellationToken ct) => WorldFederationCodec.WriteRequestAsync(
        body: body,
        ct: ct,
        kind: kind,
        stream: stream
    );
}
