using System.Buffers.Binary;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>An <see cref="IServerLink"/> whose authority is a body committed by federated transfer.</summary>
internal sealed class WorldFederatedServerLink(WorldRemoteAuthority authority) : IServerLink {
    private readonly WorldRemoteAuthority m_authority = authority;
    private readonly HashSet<int> m_unavailableBodies = [];

    private void NoteUnavailable(int bodyIndex, string reason) {
        if (m_unavailableBodies.Add(item: bodyIndex)) {
            Console.Error.WriteLine(value: $"[world.authority unavailable: body:{bodyIndex} input/submission held ({reason})]");
        }
    }
    private (WorldTcpWireFormat.DownstreamKind Kind, byte[] Body)? Submit(int bodyIndex, WorldSubmissionPayload payload) {
        if (!m_authority.TryCredential(
            bodyIndex: bodyIndex,
            mobility: out var mobility,
            sourceAuthority: out var sourceAuthority
        )) {
            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: "committed transfer credential is unavailable"
            );
            return null;
        }
        if (!WorldFrameCodec.TryEncode(
            failure: out var failure,
            frame: out var canonical,
            payload: payload
        )) {
            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: $"submission could not be encoded — {failure.Detail}"
            );
            return null;
        }

        var body = WorldFederationCodec.EncodeSubmission(
            frame: canonical,
            mobility: in mobility,
            sourceAuthority: sourceAuthority
        );
        var response = m_authority.AwaitAnswer(
            body: body,
            kind: WorldFederationRequest.Submission,
            sourceAuthority: sourceAuthority
        );

        if (response.Kind != WorldFederationResponse.Completion) {
            var narration = response.Describe();

            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: narration
            );
            return (WorldTcpWireFormat.DownstreamKind.Refusal, System.Text.Encoding.UTF8.GetBytes(s: narration));
        }

        _ = m_unavailableBodies.Remove(item: bodyIndex);

        using var input = new MemoryStream(
            response.Body,
            writable: false
        );

        return WorldTcpWireFormat.TryReadDownstreamAsync(
            ct: default,
            stream: input
        ).GetAwaiter().GetResult();
    }
    private (WorldTcpWireFormat.DownstreamKind Kind, byte[] Body)? SubmitAny(WorldSubmissionPayload payload) => Submit(
        bodyIndex: -1,
        payload: payload
    );

    public void Query(WorldQuery query, Action<QueryAnswer> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = query switch {
            WorldQuery.PlayerWhere where => (where.Index - 1),
            WorldQuery.PlayerChannels channels => (channels.Index - 1),
            WorldQuery.PlayerState state => (state.Index - 1),
            WorldQuery.PlayerTargets targets => (targets.Index - 1),
            WorldQuery.Contacts contacts => (contacts.Index - 1),
            WorldQuery.MusicState music => (music.Index - 1),
            WorldQuery.JudgeState judge => (judge.Index - 1),
            _ => -1,
        };
        var reply = Submit(
            bodyIndex: bodyIndex,
            payload: new WorldSubmissionPayload.Query(Value: query)
        );

        if (reply?.Kind != WorldTcpWireFormat.DownstreamKind.Query) {
            completion(new QueryAnswer(
                Text: ((reply is null)
                ? "remote transfer credential is unavailable"
                : WorldTcpWireFormat.DecodeText(body: reply.Value.Body)),
                Refused: true
            ));
            return;
        }

        var body = reply.Value.Body;

        if (body.Length < (sizeof(byte) + sizeof(ushort))) {
            completion(new QueryAnswer(
                Refused: true,
                Text: "remote authority returned a truncated query completion"
            ));
            return;
        }

        var offset = sizeof(byte);
        var text = WorldTcpWireFormat.ReadLengthPrefixedString(
            body: body,
            offset: ref offset,
            ok: out var ok
        );

        completion(new QueryAnswer(
            Text: (ok
            ? text
            : "remote authority returned a truncated query completion"),
            Refused: (!ok || (body[0] != 0))
        ));
    }
    public void SubmitCommand(WorldCommand command) => _ = Submit(
        bodyIndex: command.EntityIndex,
        payload: new WorldSubmissionPayload.Command(Value: command)
    );
    public void SubmitComposition(WorldComposition composition, WorldPrincipal principal) => _ = SubmitAny(payload: new WorldSubmissionPayload.Composition(Value: composition));
    public void SubmitDesignation(WorldDesignation designation, WorldPrincipal principal) => _ = Submit(
        bodyIndex: designation.EntityIndex,
        payload: new WorldSubmissionPayload.Designation(Value: designation)
    );
    public void SubmitGrant(WorldGrant grant, WorldPrincipal actor) => _ = SubmitAny(payload: new WorldSubmissionPayload.Grant(Value: grant));
    public void SubmitIntent(in IntentSubmission submission) {
        if (m_authority.TryForwardIntent(
            bodyIndex: submission.EntityIndex,
            submission: in submission,
            reason: out var reason
        )) {
            _ = m_unavailableBodies.Remove(item: submission.EntityIndex);
        } else {
            NoteUnavailable(
                bodyIndex: submission.EntityIndex,
                reason: reason
            );
        }
    }
    public void SubmitRebuild(WorldRebuildRequest request, WorldPrincipal principal) => _ = SubmitAny(payload: new WorldSubmissionPayload.Rebuild(Value: request));
    public void SubmitRevoke(WorldGrant grant, WorldPrincipal actor) => _ = SubmitAny(payload: new WorldSubmissionPayload.Revoke(Value: grant));
    public void SubmitScreenOp(WorldScreenOp op, WorldPrincipal principal) => _ = SubmitAny(payload: new WorldSubmissionPayload.ScreenOp(Value: op));
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = request switch { SessionRequest.Join join => join.Slot, SessionRequest.Leave leave => leave.Slot, SessionRequest.SetIdentity identity => identity.Slot, _ => -1 };
        var reply = Submit(
            bodyIndex: bodyIndex,
            payload: new WorldSubmissionPayload.Session(Value: request)
        );

        if (reply?.Kind != WorldTcpWireFormat.DownstreamKind.Session) {
            completion(new SessionReply(
                false,
                -1,
                string.Empty,
                ((reply is null)
                ? "remote transfer credential is unavailable"
                : WorldTcpWireFormat.DecodeText(body: reply.Value.Body))
            ));
            return;
        }

        var body = reply.Value.Body;

        if (body.Length < ((sizeof(byte) + sizeof(int)) + sizeof(ushort))) {
            completion(new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: "remote authority returned a truncated session completion",
                RosterEcho: string.Empty
            ));
            return;
        }

        var offset = (sizeof(byte) + sizeof(int));
        var reason = WorldTcpWireFormat.ReadLengthPrefixedString(
            body: body,
            offset: ref offset,
            ok: out var ok
        );

        completion(new SessionReply(
            (ok && (body[0] != 0)),
            BinaryPrimitives.ReadInt32LittleEndian(source: body.AsSpan(start: sizeof(byte))),
            string.Empty,
            (ok
            ? reason
            : "remote authority returned a truncated session completion")
        ));
    }
    public void SubmitSessionLever(WorldSessionLever lever, WorldPrincipal principal) => _ = SubmitAny(payload: new WorldSubmissionPayload.Lever(Value: lever));
    public void SubmitUndo(int count, WorldPrincipal principal) => _ = SubmitAny(payload: new WorldSubmissionPayload.Undo(Count: count));
    public void SubmitWorldMutation(WorldMutation mutation) => _ = SubmitAny(payload: new WorldSubmissionPayload.Mutation(Value: mutation));
}
