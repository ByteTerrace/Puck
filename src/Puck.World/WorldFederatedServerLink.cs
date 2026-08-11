using System.Buffers.Binary;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>An <see cref="IServerLink"/> whose authority is a body committed by federated transfer.</summary>
internal sealed class WorldFederatedServerLink(WorldRemoteAuthority authority) : IServerLink {
    private readonly WorldRemoteAuthority m_authority = authority;

    public void SubmitIntent(in IntentSubmission submission) {
        if (!m_authority.TryCredential(bodyIndex: submission.EntityIndex, sourceAuthority: out var sourceAuthority, transferId: out var transferId, ordinal: out var ordinal)) {
            return;
        }
        _ = m_authority.RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Intent, body: WorldFederationWireFormat.EncodeIntent(sourceAuthority: sourceAuthority, transferId: transferId, ordinal: ordinal, submission: in submission));
    }

    public void SubmitCommand(WorldCommand command) => _ = Submit(bodyIndex: command.EntityIndex, payload: new WorldSubmissionPayload.Command(command));
    public void SubmitDesignation(WorldDesignation designation, WorldPrincipal principal) => _ = Submit(bodyIndex: designation.EntityIndex, payload: new WorldSubmissionPayload.Designation(designation));
    public void SubmitRebuild(WorldRebuildRequest request, WorldPrincipal principal) => _ = SubmitAny(new WorldSubmissionPayload.Rebuild(request));
    public void SubmitWorldMutation(WorldMutation mutation) => _ = SubmitAny(new WorldSubmissionPayload.Mutation(mutation));
    public void SubmitUndo(int count, WorldPrincipal principal) => _ = SubmitAny(new WorldSubmissionPayload.Undo(count));
    public void SubmitGrant(WorldGrant grant, WorldPrincipal actor) => _ = SubmitAny(new WorldSubmissionPayload.Grant(grant));
    public void SubmitRevoke(WorldGrant grant, WorldPrincipal actor) => _ = SubmitAny(new WorldSubmissionPayload.Revoke(grant));
    public void SubmitComposition(WorldComposition composition, WorldPrincipal principal) => _ = SubmitAny(new WorldSubmissionPayload.Composition(composition));
    public void SubmitSessionLever(WorldSessionLever lever, WorldPrincipal principal) => _ = SubmitAny(new WorldSubmissionPayload.Lever(lever));
    public void SubmitAddonLifecycle(WorldAddonLifecycle lifecycle, WorldPrincipal principal) => _ = SubmitAny(new WorldSubmissionPayload.AddonLifecycle(lifecycle));
    public void SubmitScreenOp(WorldScreenOp op, WorldPrincipal principal) => _ = SubmitAny(new WorldSubmissionPayload.ScreenOp(op));

    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = request switch { SessionRequest.Join join => join.Slot, SessionRequest.Leave leave => leave.Slot, SessionRequest.SetIdentity identity => identity.Slot, _ => -1 };
        var reply = Submit(bodyIndex: bodyIndex, payload: new WorldSubmissionPayload.Session(request));

        if (reply?.Kind != WorldTcpWireFormat.DownstreamKind.Session) {
            completion(new SessionReply(false, -1, string.Empty, reply is null ? "remote transfer credential is unavailable" : WorldTcpWireFormat.DecodeText(reply.Value.Body)));
            return;
        }

        var body = reply.Value.Body;
        var offset = (sizeof(byte) + sizeof(int));
        completion(new SessionReply(body[0] != 0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(start: sizeof(byte))), string.Empty, WorldTcpWireFormat.ReadLengthPrefixedString(body: body, offset: ref offset)));
    }

    public void Query(WorldQuery query, Action<QueryAnswer> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = query switch { WorldQuery.PlayerWhere where => (where.Index - 1), _ => -1 };
        var reply = Submit(bodyIndex: bodyIndex, payload: new WorldSubmissionPayload.Query(query));

        if (reply?.Kind != WorldTcpWireFormat.DownstreamKind.Query) {
            completion(new QueryAnswer(Text: (reply is null ? "remote transfer credential is unavailable" : WorldTcpWireFormat.DecodeText(reply.Value.Body)), Refused: true));
            return;
        }

        var body = reply.Value.Body;
        var offset = sizeof(byte);
        completion(new QueryAnswer(Text: WorldTcpWireFormat.ReadLengthPrefixedString(body: body, offset: ref offset), Refused: (body[0] != 0)));
    }

    private (WorldTcpWireFormat.DownstreamKind Kind, byte[] Body)? SubmitAny(WorldSubmissionPayload payload) => Submit(bodyIndex: -1, payload: payload);

    private (WorldTcpWireFormat.DownstreamKind Kind, byte[] Body)? Submit(int bodyIndex, WorldSubmissionPayload payload) {
        if (!m_authority.TryCredential(bodyIndex: bodyIndex, sourceAuthority: out var sourceAuthority, transferId: out var transferId, ordinal: out var ordinal) ||
            !WorldFrameCodec.TryEncode(payload: payload, frame: out var canonical, failure: out _)) {
            return null;
        }

        var body = WorldFederationWireFormat.EncodeSubmission(sourceAuthority: sourceAuthority, transferId: transferId, ordinal: ordinal, frame: canonical);
        var response = m_authority.RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Submission, body: body);
        if (response.Kind != WorldFederationWireFormat.ResponseKind.Completion) {
            return (WorldTcpWireFormat.DownstreamKind.Refusal, response.Body);
        }

        using var input = new MemoryStream(response.Body, writable: false);
        return WorldTcpWireFormat.TryReadDownstreamAsync(stream: input, ct: default).GetAwaiter().GetResult();
    }
}
