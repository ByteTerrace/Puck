using System.Text;
using Puck.Networking;
using Puck.World.Server;

namespace Puck.World.Protocol;

/// <summary>
/// The remote socket's downstream reply grammar — shared by <c>Puck.World.Server.WorldPeerHost</c> (the server
/// door) and the <c>--connect</c> peer clients, so both sides frame bytes identically without a second definition
/// drifting from the first. Upstream (client → server, after Hello) rides the existing <see cref="WorldFrameCodec"/>
/// grammar over <see cref="HandshakeWireFormat.TryReadLengthPrefixedFrameAsync"/>'s raw read; Hello and identity
/// ride <see cref="HandshakeWireFormat"/> directly. Downstream (server → client) is this type's own, deliberately
/// small v1 grammar: a Hello verdict once, then one completion per submitted frame (this v1 socket is strictly
/// request-then-response per connection, so no correlation id travels on the wire) — not one of
/// <see cref="WorldSubmissionCodec"/>'s twelve leaf kinds, since v1 carries only the Completion lane (streamed
/// snapshots/definitions/compositions/levers are not carried here).
/// </summary>
public static class WorldPeerWireFormat {
    /// <summary>The hard cap on a downstream frame's total bytes. Typed reflow proposals remain bounded by this
    /// same cap and never become unbounded bulk payloads.</summary>
    public const int MaxDownstreamFrameBytes = (64 * 1024);
    /// <summary>The hard cap on an upstream frame's total bytes (prefix + payload) — generous enough for the largest
    /// leaf (<c>Definition</c>, 16 MiB) while still refusing an absurd length before allocating for it.</summary>
    public const int MaxUpstreamFrameBytes = (((16 * 1024) * 1024) + WorldFrameCodec.PrefixBytes);

    /// <summary>The downstream message kinds — Hello's two outcomes, then one per <see cref="WorldSubmissionResult"/>
    /// case, plus a codec/apply-level refusal for a frame that never reached a typed result at all.</summary>
    public enum DownstreamKind : byte {
        /// <summary>The Hello door accepted this connection and admitted a peer body.</summary>
        HelloAccepted,

        /// <summary>The Hello door refused this connection — the socket closes right after.</summary>
        HelloRefused,

        /// <summary>The protocol-version check passed; this is the admission door's fresh challenge nonce (see
        /// <see cref="WorldAdmissionDoor.NewChallenge"/>). The peer answers with a HelloIdentity frame
        /// (<see cref="HandshakeWireFormat.WriteHelloIdentityAsync"/>/<see cref="HandshakeWireFormat.TryReadHelloIdentityAsync"/>)
        /// before either <see cref="HelloAccepted"/> or <see cref="HelloRefused"/> follows.</summary>
        HelloChallenge,

        /// <summary><see cref="WorldSubmissionResult.Ack"/> — the envelope finished draining; no data.</summary>
        Ack,

        /// <summary><see cref="WorldSubmissionResult.Session"/> — a <see cref="SessionReply"/>.</summary>
        Session,

        /// <summary><see cref="WorldSubmissionResult.Query"/> — a <see cref="QueryAnswer"/>.</summary>
        Query,

        /// <summary>The submitted frame refused before it ever became a typed result (a codec refusal, an
        /// unadmitted/mismatched principal, or a capacity refusal).</summary>
        Refusal,

        /// <summary><see cref="WorldSubmissionResult.Query"/> carrying a typed
        /// <see cref="WorldPlacementProposal"/> beside its review text.</summary>
        QueryPlacementProposal,

        /// <summary><see cref="WorldSubmissionResult.Mutation"/> carrying the applied/refused decision and its
        /// independent persistence status.</summary>
        MutationOutcome,
    }

    private static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(s: (text ?? string.Empty));
    private static Task WriteDownstreamAsync(Stream stream, DownstreamKind kind, ReadOnlyMemory<byte> body, CancellationToken ct) => WireFrame.WriteAsync(
        body: body,
        ct: ct,
        kind: ((byte)kind),
        stream: stream
    );

    /// <summary>Decodes a whole downstream body as raw UTF-8 text — the shape <see cref="EncodeText"/> writes for
    /// <see cref="DownstreamKind.HelloRefused"/> and <see cref="DownstreamKind.Refusal"/>: a single text field with
    /// no internal length prefix, since the outer frame's own u32 length already delimits it.</summary>
    /// <param name="body">The whole downstream body.</param>
    /// <returns>The decoded string.</returns>
    public static string DecodeText(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(bytes: body);
    /// <summary>Decodes one whole downstream frame already in memory — <c>[u32 length][u8 kind][body]</c>, exactly
    /// the bytes <see cref="WriteResultAsync"/> and its siblings write — into its kind and body. The one parse both
    /// <see cref="TryReadDownstreamAsync"/> (over a socket) and the federation Completion lane (which embeds a whole
    /// downstream frame as a response body) share, so the grammar has one home.</summary>
    /// <param name="frame">The whole frame, prefix included.</param>
    /// <param name="kind">The decoded kind on success.</param>
    /// <param name="body">The body, sliced over <paramref name="frame"/> without copying, on success.</param>
    /// <returns><see langword="false"/> when the frame is shorter than its prefix plus kind byte, longer than
    /// <see cref="MaxDownstreamFrameBytes"/>, declares a length other than the bytes that follow the prefix, or names
    /// an undeclared kind.</returns>
    public static bool TryDecodeDownstream(ReadOnlyMemory<byte> frame, out DownstreamKind kind, out ReadOnlyMemory<byte> body) {
        kind = default;
        body = ReadOnlyMemory<byte>.Empty;

        if (
            (frame.Length <= sizeof(uint)) ||
            (frame.Length > MaxDownstreamFrameBytes) ||
            (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source: frame.Span) != ((uint)(frame.Length - sizeof(uint))))
        ) {
            return false;
        }

        var declared = ((DownstreamKind)frame.Span[sizeof(uint)]);

        if (!Enum.IsDefined(value: declared)) {
            return false;
        }

        kind = declared;
        body = frame[(sizeof(uint) + sizeof(byte))..];

        return true;
    }
    /// <summary>Reads one downstream frame — the client's one reader for both the Hello verdict and every completion.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The decoded kind and its raw body — a slice over the frame's own buffer, allocated per frame and never
    /// reused, so it is safe to keep — or <see langword="null"/> on disconnect or a frame that does not decode.</returns>
    public static async Task<(DownstreamKind Kind, ReadOnlyMemory<byte> Body)?> TryReadDownstreamAsync(Stream stream, CancellationToken ct) {
        var whole = await HandshakeWireFormat.TryReadLengthPrefixedFrameAsync(
            ct: ct,
            maxTotalBytes: MaxDownstreamFrameBytes,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (((whole is not null) && TryDecodeDownstream(
            body: out var body,
            frame: whole,
            kind: out var kind
        ))
            ? (kind, body)
            : null
        );
    }
    /// <summary>Writes a downstream Hello-accepted verdict.</summary>
    public static Task WriteHelloAcceptedAsync(Stream stream, int peerIndex, int generation, int connectionId, CancellationToken ct) {
        var writer = new WireWriter(capacity: (3 * sizeof(int)));

        writer.WriteInt32(value: peerIndex);
        writer.WriteInt32(value: generation);
        writer.WriteInt32(value: connectionId);

        return WriteDownstreamAsync(
            body: writer.WrittenMemory,
            ct: ct,
            kind: DownstreamKind.HelloAccepted,
            stream: stream
        );
    }
    /// <summary>Writes the downstream Hello-challenge frame: a fresh admission nonce
    /// (<see cref="WorldAdmissionDoor.NewChallenge"/>). Sent once the protocol-version check passes and
    /// before any identity is asked for.</summary>
    public static Task WriteHelloChallengeAsync(Stream stream, byte[] challenge, CancellationToken ct) =>
        WriteDownstreamAsync(
            body: challenge,
            ct: ct,
            kind: DownstreamKind.HelloChallenge,
            stream: stream
        );
    /// <summary>Writes a downstream Hello-refused verdict (the caller closes the socket right after).</summary>
    public static Task WriteHelloRefusedAsync(Stream stream, string reason, CancellationToken ct) =>
        WriteDownstreamAsync(
            stream: stream,
            kind: DownstreamKind.HelloRefused,
            body: EncodeText(text: reason),
            ct: ct
        );
    /// <summary>Writes a downstream refusal for a frame that never reached a typed result.</summary>
    public static Task WriteRefusalAsync(Stream stream, string reason, CancellationToken ct) =>
        WriteDownstreamAsync(
            stream: stream,
            kind: DownstreamKind.Refusal,
            body: EncodeText(text: reason),
            ct: ct
        );
    /// <summary>Writes a downstream <see cref="WorldSubmissionResult"/> as the v1 Completion lane.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="result">The typed submission result.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static Task WriteResultAsync(Stream stream, WorldSubmissionResult result, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: result);

        switch (result) {
            case WorldSubmissionResult.Ack:
                return WriteDownstreamAsync(
                    body: ReadOnlyMemory<byte>.Empty,
                    ct: ct,
                    kind: DownstreamKind.Ack,
                    stream: stream
                );
            case WorldSubmissionResult.Session session: {
                    // [u8 Accepted][i32 AssignedIndex][u16 reasonLen][reason utf8] — WireWriter.WriteBoolean/
                    // WriteInt32/WriteString lay out exactly this shape.
                    var writer = new WireWriter();

                    writer.WriteBoolean(value: session.Reply.Accepted);
                    writer.WriteInt32(value: session.Reply.AssignedIndex);
                    writer.WriteString(value: session.Reply.Reason);

                    return WriteDownstreamAsync(
                        body: writer.WrittenMemory,
                        ct: ct,
                        kind: DownstreamKind.Session,
                        stream: stream
                    );
                }
            case WorldSubmissionResult.Query query: {
                    if (query.Answer.Payload is WorldPlacementProposal) {
                        if (!TryEncodePlacementProposalAnswer(
                            answer: query.Answer,
                            body: out var proposalBody,
                            reason: out var proposalFailure
                        )) {
                            return WriteDownstreamAsync(
                                stream: stream,
                                kind: DownstreamKind.Refusal,
                                body: EncodeText(text: proposalFailure),
                                ct: ct
                            );
                        }

                        return WriteDownstreamAsync(
                            body: proposalBody,
                            ct: ct,
                            kind: DownstreamKind.QueryPlacementProposal,
                            stream: stream
                        );
                    }

                    // [u8 Refused][u16 textLen][text utf8].
                    var writer = new WireWriter();

                    writer.WriteBoolean(value: query.Answer.Refused);
                    writer.WriteString(value: query.Answer.Text);

                    return WriteDownstreamAsync(
                        body: writer.WrittenMemory,
                        ct: ct,
                        kind: DownstreamKind.Query,
                        stream: stream
                    );
                }
            case WorldSubmissionResult.Mutation mutation: {
                    if (!mutation.Outcome.IsValid) {
                        return WriteDownstreamAsync(
                            stream: stream,
                            kind: DownstreamKind.Refusal,
                            body: EncodeText(text: "mutation outcome is malformed"),
                            ct: ct
                        );
                    }

                    try {
                        var writer = new WireWriter();
                        writer.WriteString(value: mutation.Outcome.OperationId.ToString("D"));
                        writer.WriteString(value: mutation.Outcome.Actor.Describe());
                        writer.WriteString(value: mutation.Outcome.PayloadDigest);
                        writer.WriteByte(value: (byte)mutation.Outcome.Decision);
                        writer.WriteByte(value: (byte)mutation.Outcome.PersistenceStatus);
                        writer.WriteString(value: mutation.Outcome.Code);
                        writer.WriteString(value: mutation.Outcome.Detail);
                        writer.WriteBoolean(value: mutation.Outcome.AffectedGroupRevision.HasValue);
                        if (mutation.Outcome.AffectedGroupRevision is { } groupRevision) {
                            writer.WriteInt64(value: groupRevision);
                        }
                        writer.WriteBoolean(value: mutation.Outcome.DurableWatermark.HasValue);
                        if (mutation.Outcome.DurableWatermark is { } watermark) {
                            writer.WriteInt64(value: watermark.RootSequence);
                            writer.WriteBoolean(value: watermark.CheckpointOrdinal.HasValue);
                            if (watermark.CheckpointOrdinal is { } checkpointOrdinal) {
                                writer.WriteInt64(value: checkpointOrdinal);
                            }
                            writer.WriteBoolean(value: watermark.JournalSequence.HasValue);
                            if (watermark.JournalSequence is { } journalSequence) {
                                writer.WriteInt64(value: journalSequence);
                            }
                            writer.WriteUInt64(value: watermark.Tick);
                        }

                        return WriteDownstreamAsync(
                            body: writer.WrittenMemory,
                            ct: ct,
                            kind: DownstreamKind.MutationOutcome,
                            stream: stream
                        );
                    } catch (ArgumentException exception) {
                        return WriteDownstreamAsync(
                            stream: stream,
                            kind: DownstreamKind.Refusal,
                            body: EncodeText(text: $"mutation outcome is not encodable: {exception.Message}"),
                            ct: ct
                        );
                    }
                }
            default:
                return WriteDownstreamAsync(
                    stream: stream,
                    kind: DownstreamKind.Refusal,
                    body: EncodeText(text: $"no downstream encoding for {result.GetType().Name}"),
                    ct: ct
                );
        }
    }
    /// <summary>Decodes a <paramref name="kind"/>/<paramref name="body"/> pair from <see cref="TryReadDownstreamAsync"/>
    /// back into its typed <see cref="WorldSubmissionResult"/> — the read-side twin of <see cref="WriteResultAsync"/>,
    /// shared by every consumer that turns a peer's completion frame into a result rather than re-deriving the field
    /// offsets per call site.</summary>
    /// <param name="kind">The downstream kind.</param>
    /// <param name="body">The frame body.</param>
    /// <param name="result">The decoded result on success.</param>
    /// <param name="reason">The refusal text — the peer's own refusal narration for <see cref="DownstreamKind.Refusal"/>,
    /// a truncation detail for a malformed <see cref="DownstreamKind.Session"/>/<see cref="DownstreamKind.Query"/> body,
    /// or empty on success.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> decoded.</returns>
    public static bool TryReadResult(DownstreamKind kind, ReadOnlySpan<byte> body, out WorldSubmissionResult? result, out string reason) {
        switch (kind) {
            case DownstreamKind.Ack:
                result = WorldSubmissionResult.Ack.Instance;
                reason = string.Empty;

                return true;
            case DownstreamKind.Session: {
                    var reader = new WireReader(bytes: body);
                    var accepted = reader.ReadBoolean();
                    var assignedIndex = reader.ReadInt32();
                    var sessionReason = reader.ReadString(field: "session completion reason");

                    if (!reader.TryFinish(failure: out _)) {
                        result = null;
                        reason = "remote authority returned a truncated session completion";

                        return false;
                    }

                    result = new WorldSubmissionResult.Session(Reply: new SessionReply(
                        Accepted: accepted,
                        AssignedIndex: assignedIndex,
                        Reason: sessionReason,
                        RosterEcho: string.Empty
                    ));
                    reason = string.Empty;

                    return true;
                }
            case DownstreamKind.Query: {
                    var reader = new WireReader(bytes: body);
                    var refused = reader.ReadBoolean();
                    var text = reader.ReadString(field: "query completion text");

                    if (!reader.TryFinish(failure: out _)) {
                        result = null;
                        reason = "remote authority returned a truncated query completion";

                        return false;
                    }

                    result = new WorldSubmissionResult.Query(Answer: new QueryAnswer(
                        Text: text,
                        Refused: refused
                    ));
                    reason = string.Empty;

                    return true;
                }
            case DownstreamKind.QueryPlacementProposal: {
                    if (body.Length > MaxDownstreamBodyBytes) {
                        result = null;
                        reason = $"typed reflow proposal carries {body.Length} bytes; cap is {MaxDownstreamBodyBytes}";

                        return false;
                    }

                    var reader = new WireReader(bytes: body);
                    var refused = reader.ReadBoolean();
                    var text = reader.ReadString(field: "query completion text");
                    var mutationBytes = reader.ReadBlock(
                        field: "reflow proposal mutation",
                        maxBytes: MaxDownstreamBodyBytes
                    );
                    var candidates = reader.ReadInt32();
                    var moved = reader.ReadInt32();
                    var cost = reader.ReadInt64();
                    var affectedIds = ReadProposalStrings(
                        field: "reflow proposal affected ids",
                        reader: ref reader
                    );
                    var constraints = ReadProposalStrings(
                        field: "reflow proposal constraints",
                        reader: ref reader
                    );

                    if (!reader.TryFinish(failure: out var wireFailure)) {
                        result = null;
                        reason = wireFailure.Detail;

                        return false;
                    }
                    if ((candidates < 0) || (moved < 0) || (cost < 0)) {
                        result = null;
                        reason = "reflow proposal metadata carries a negative count or cost";

                        return false;
                    }
                    if (!WorldSubmissionCodec.TryDecodeMutation(
                        bytes: mutationBytes,
                        mutation: out var mutation,
                        failure: out var mutationFailure
                    ) || mutation is not WorldMutation.Batch batch) {
                        result = null;
                        reason = $"reflow proposal mutation is invalid: {mutationFailure}";

                        return false;
                    }
                    if (!batch.TryValidateShape(out var batchReason)) {
                        result = null;
                        reason = $"reflow proposal batch is malformed: {batchReason}";

                        return false;
                    }

                    result = new WorldSubmissionResult.Query(Answer: new QueryAnswer(
                        Text: text,
                        Refused: refused,
                        Payload: new WorldPlacementProposal(
                            Mutation: batch,
                            Candidates: candidates,
                            Moved: moved,
                            Cost: cost
                        ) {
                            AffectedIds = affectedIds,
                            Constraints = constraints
                        }
                    ));
                    reason = string.Empty;

                    return true;
                }
            case DownstreamKind.MutationOutcome: {
                    var reader = new WireReader(bytes: body);
                    var operationText = reader.ReadRequiredString(field: "mutation operation id", maxBytes: 64);
                    var actorText = reader.ReadRequiredString(field: "mutation actor", maxBytes: 256);
                    var payloadDigest = reader.ReadRequiredString(field: "mutation payload digest", maxBytes: 128);
                    var decision = (WorldMutationDecision)reader.ReadByte();
                    var persistence = (WorldMutationPersistenceStatus)reader.ReadByte();
                    var code = reader.ReadRequiredString(field: "mutation decision code", maxBytes: 256);
                    var detail = reader.ReadString(field: "mutation decision detail", maxBytes: 4096);
                    var hasGroupRevision = reader.ReadBoolean();
                    var groupRevision = (hasGroupRevision ? reader.ReadInt64() : (long?)null);
                    var hasWatermark = reader.ReadBoolean();
                    var watermark = (hasWatermark
                        ? new WorldDurableWatermark(
                            RootSequence: reader.ReadInt64(),
                            CheckpointOrdinal: (reader.ReadBoolean() ? reader.ReadInt64() : (long?)null),
                            JournalSequence: (reader.ReadBoolean() ? reader.ReadInt64() : (long?)null),
                            Tick: reader.ReadUInt64()
                        )
                        : (WorldDurableWatermark?)null);

                    if (!reader.TryFinish(failure: out var wireFailure)) {
                        result = null;
                        reason = wireFailure.Detail;
                        return false;
                    }
                    if (!Guid.TryParseExact(operationText, "D", out var operationId) || operationId == Guid.Empty) {
                        result = null;
                        reason = "mutation operation id is not a canonical GUID";
                        return false;
                    }
                    if (!WorldPrincipal.TryParse(actorText, out var actor) || !actor.IsCanonical()) {
                        result = null;
                        reason = "mutation actor is not a canonical principal";
                        return false;
                    }
                    if (!Enum.IsDefined(decision) || !Enum.IsDefined(persistence) ||
                        !WorldMutationBinding.IsSha256Hex(payloadDigest) ||
                        groupRevision is < 0 || watermark is { IsValid: false }) {
                        result = null;
                        reason = "mutation outcome carries an invalid enum, digest, revision, or watermark";
                        return false;
                    }

                    var outcome = new WorldMutationOutcome(
                        operationId,
                        actor,
                        payloadDigest,
                        decision,
                        code,
                        detail,
                        groupRevision,
                        persistence,
                        watermark
                    );
                    if (!outcome.IsValid) {
                        result = null;
                        reason = "mutation outcome is malformed";
                        return false;
                    }

                    result = new WorldSubmissionResult.Mutation(Outcome: outcome);
                    reason = string.Empty;
                    return true;
                }
            case DownstreamKind.Refusal:
                result = null;
                reason = DecodeText(body: body);

                return false;
            default:
                result = null;
                reason = $"no downstream decoding for {kind}";

                return false;
        }
    }

    private const int MaxDownstreamBodyBytes = MaxDownstreamFrameBytes - sizeof(uint) - sizeof(byte);
    private const int MaxProposalMetadataItems = 256;

    private static bool TryEncodePlacementProposalAnswer(QueryAnswer answer, out byte[] body, out string reason) {
        body = [];
        reason = string.Empty;
        if (answer.Payload is not WorldPlacementProposal proposal) {
            reason = "query payload is not a placement proposal";

            return false;
        }
        if (!WorldSubmissionCodec.TryEncodeMutation(
            mutation: proposal.Mutation,
            bytes: out var mutationBytes,
            failure: out var mutationFailure
        )) {
            reason = $"reflow proposal mutation is not encodable: {mutationFailure}";

            return false;
        }
        if (!proposal.Mutation.TryValidateShape(out var batchReason)) {
            reason = $"reflow proposal batch is malformed: {batchReason}";

            return false;
        }
        if (mutationBytes.Length > MaxDownstreamBodyBytes) {
            reason = $"reflow proposal mutation carries {mutationBytes.Length} bytes; cap is {MaxDownstreamBodyBytes}";

            return false;
        }
        if (!TryValidateProposalStrings(proposal.AffectedIds, "affected ids", out reason) ||
            !TryValidateProposalStrings(proposal.Constraints, "constraints", out reason)) {
            return false;
        }

        try {
            var writer = new WireWriter(capacity: Math.Min(MaxDownstreamBodyBytes, mutationBytes.Length + 1024));
            writer.WriteBoolean(value: answer.Refused);
            writer.WriteString(value: answer.Text);
            writer.WriteBlock(value: mutationBytes);
            writer.WriteInt32(value: proposal.Candidates);
            writer.WriteInt32(value: proposal.Moved);
            writer.WriteInt64(value: proposal.Cost);
            WriteProposalStrings(writer: writer, values: proposal.AffectedIds);
            WriteProposalStrings(writer: writer, values: proposal.Constraints);
            if (writer.Length > MaxDownstreamBodyBytes) {
                reason = $"typed reflow proposal carries {writer.Length} bytes; cap is {MaxDownstreamBodyBytes}";

                return false;
            }

            body = writer.ToArray();

            return true;
        } catch (ArgumentException exception) {
            reason = $"reflow proposal metadata is not encodable: {exception.Message}";

            return false;
        }
    }

    private static bool TryValidateProposalStrings(IReadOnlyList<string>? values, string field, out string reason) {
        if (values is null || values.Count > MaxProposalMetadataItems || values.Any(value => value is null)) {
            reason = $"reflow proposal {field} exceed the metadata bound or contain null entries";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    private static void WriteProposalStrings(WireWriter writer, IReadOnlyList<string> values) {
        writer.WriteInt32(value: values.Count);
        foreach (var value in values) { writer.WriteString(value: value); }
    }

    private static string[] ReadProposalStrings(string field, ref WireReader reader) {
        var count = reader.ReadCount(field: field, minimum: 0, maximum: MaxProposalMetadataItems);
        var values = new string[count];
        for (var index = 0; index < count; index++) {
            values[index] = reader.ReadString(field: $"{field}[{index}]");
        }

        return values;
    }

}
