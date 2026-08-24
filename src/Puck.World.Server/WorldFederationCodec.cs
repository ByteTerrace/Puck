using System.Text;
using System.Text.Json;
using Puck.Attestation;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>The federation request kinds an authority accepts from an authenticated peer authority.</summary>
public enum WorldFederationRequest : byte {
    /// <summary>Attach as a projection observer and stream definition/snapshot records until the socket closes.</summary>
    Observe = 1,

    /// <summary>Ask the destination to hold capacity for a traveling cohort.</summary>
    Reserve = 2,

    /// <summary>Land a previously reserved cohort.</summary>
    Commit = 3,

    /// <summary>Release a reservation without landing it.</summary>
    Abort = 4,

    /// <summary>Forward one typed submission for a committed traveler.</summary>
    Submission = 5,

    /// <summary>Publish one per-tick intent image for a committed traveler.</summary>
    Intent = 6,

    /// <summary>Answer the connection challenge with a source authority and its proof.</summary>
    Authenticate = 7,

    /// <summary>Ask the destination's idempotent verdict for one transfer id.</summary>
    Status = 8,

    /// <summary>Resolve the final observable authority epoch behind a committed traveler.</summary>
    Route = 9,

    /// <summary>Open the persistent intent lane on this connection.</summary>
    IntentStream = 10,

    /// <summary>Close the intent lane as a deliberate route handoff rather than a dropped client.</summary>
    IntentStreamHandoff = 11,

    /// <summary>Confirm the source has consumed a committed transfer.</summary>
    AcknowledgeTransfer = 12,
}
/// <summary>The federation response kinds an authority writes back.</summary>
public enum WorldFederationResponse : byte {
    /// <summary>A canonical world definition revision.</summary>
    Definition = 1,

    /// <summary>One per-tick projection record.</summary>
    Snapshot = 2,

    /// <summary>A reservation verdict.</summary>
    Reservation = 3,

    /// <summary>A commit verdict.</summary>
    Commit = 4,

    /// <summary>An empty acknowledgement.</summary>
    Ack = 5,

    /// <summary>A named refusal and its narration.</summary>
    Refusal = 6,

    /// <summary>A forwarded submission's downstream completion.</summary>
    Completion = 7,

    /// <summary>The connection's authentication challenge nonce.</summary>
    Challenge = 8,

    /// <summary>A transfer's idempotent status byte.</summary>
    Status = 9,

    /// <summary>A committed traveler's route description.</summary>
    Route = 10,
}
/// <summary>The stable names a federation authority refuses under. A refusal frame's text always opens with one of
/// these, so a peer and a read-back can both count refusals by name rather than by sentence.</summary>
public enum WorldFederationRefusal : byte {
    /// <summary>This authority carries no federation credentials, so it denies federation outright.</summary>
    AuthenticationUnconfigured,

    /// <summary>The presented source authority and proof did not verify against the issued challenge.</summary>
    AuthenticationFailed,

    /// <summary>The frame's prefix, kind, or leaf bytes did not decode.</summary>
    FrameMalformed,

    /// <summary>The frame's kind is declared but not accepted in this position on this connection.</summary>
    RequestUnsupported,

    /// <summary>The leaf's carried source authority is not the one this connection authenticated as.</summary>
    SourceAuthorityMismatch,

    /// <summary>A frame that must carry no payload carried one.</summary>
    PayloadUnexpected,

    /// <summary>The destination refused the reservation.</summary>
    ReservationRefused,

    /// <summary>The credential names no committed transfer body at this authority.</summary>
    CredentialUnknown,

    /// <summary>The traveler has neither a live body here nor a committed onward route.</summary>
    RouteUnknown,

    /// <summary>The forwarded submission did not reach a typed result.</summary>
    SubmissionRefused,

    /// <summary>The intent update named no live or forwarded transfer body.</summary>
    IntentRefused,

    /// <summary>The persistent lane to this peer is not carrying traffic.</summary>
    LaneUnavailable,
}
/// <summary>
/// The one authority-to-authority codec. It carries the same frame grammar, bounded reader, and named refusal
/// vocabulary every other World wire surface uses (<see cref="WireFrame"/>, <see cref="WireReader"/>,
/// <see cref="WireRefusal"/>) — a federated peer is untrusted input like any other, so every decoder here is
/// bounded and Try-shaped and none of them throws on hostile bytes. Local colocation invokes the same server methods
/// directly; this codec is only the transport underneath that contract.
/// </summary>
public static class WorldFederationCodec {
    /// <summary>The hard ceiling on any framed federation payload, applied before a frame body is allocated. The
    /// per-kind caps below refuse the rest by name.</summary>
    public const int MaxFrameBytes = ((32 * 1024) * 1024);
    /// <summary>The hard cap on one authenticated proof block — sized for a wrapped claim plus a two-hop chain
    /// (<see cref="AttestationChainEnvelope"/> around three ecdsa-p256-sha256 attestations) with comfortable
    /// headroom, never for one HMAC tag.</summary>
    public const int MaxProofBytes = (8 * 1024);
    /// <summary>The protocol discriminator, distinct from the interactive peer wire key so one listener can route
    /// both dialects off the first eight bytes.</summary>
    public const ulong WireKey = 0x35444546554B4350UL;

    private static bool Finish(ref WireReader reader, out WireFailure failure) => reader.TryFinish(failure: out failure);
    private static WorldTransferCommitMember ReadCommitMember(ref WireReader reader, WorldPlayerDefaults defaults, int ordinal) {
        var profileBytes = reader.ReadBlock(
            field: $"commit traveler {(ordinal + 1)} identity document",
            maxBytes: WireLimits.MaxDocumentBytes
        );
        WorldIdentity? profile = null;

        if (
            !reader.Failed &&
            (profileBytes.Length > 0)
        ) {
            if (
                TryDeserializeDefinition(
                bytes: profileBytes,
                definition: out var profileDocument,
                failure: out _,
                field: $"commit traveler {(ordinal + 1)} identity document"
            ) &&
                (profileDocument is not null)
            ) {
                profile = new WorldIdentity(
                    defaults: defaults,
                    document: profileDocument
                );
            } else {
                reader.Fail(
                    detail: $"commit traveler {(ordinal + 1)} identity document did not parse",
                    refusal: WireRefusal.PayloadMalformed
                );
            }
        }

        var mapped = reader.ReadBoolean();
        var program = reader.ReadString(field: "commit body motion program");
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var planar = reader.ReadFixedVector();
        var vertical = reader.ReadFixed();
        var channelCount = reader.ReadCount(
            field: "commit channel count",
            maximum: ChannelLimits.MaxChannels,
            minimum: 0
        );
        var channels = new WorldTransferChannelEdge[(reader.Failed
            ? 0
            : channelCount)];

        for (var index = 0; (index < channels.Length); index++) {
            channels[index] = new WorldTransferChannelEdge(
                Name: reader.ReadString(field: "commit channel name"),
                PreviousBit: reader.ReadBoolean(),
                HeldValue: reader.ReadFixed()
            );
        }

        var registerCount = reader.ReadCount(
            field: "commit action register count",
            maximum: ChannelLimits.MaxChannels,
            minimum: 0
        );
        var registers = new WorldTransferActionRegister[(reader.Failed
            ? 0
            : registerCount)];

        for (var index = 0; (index < registers.Length); index++) {
            var name = reader.ReadString(field: "commit action register name");
            var kind = ((ActionStateKind)reader.ReadByte());

            if (!Enum.IsDefined(value: kind)) {
                reader.Fail(
                    detail: $"commit action register kind {((byte)kind)} is not declared",
                    refusal: WireRefusal.EnumValueUnknown
                );
            }

            registers[index] = new WorldTransferActionRegister(
                Name: name,
                Kind: kind,
                Value: reader.ReadFixed(),
                TimerTicks: reader.ReadUInt64()
            );
        }

        WorldContinuumTrajectory? continuum = null;

        if (reader.ReadBoolean()) {
            continuum = ReadContinuum(reader: ref reader);
        }

        return new WorldTransferCommitMember(
            profile,
            mapped,
            program,
            position,
            yaw,
            planar,
            vertical,
            new WorldTransferActionContinuity(
                Channels: channels,
                Registers: registers
            ),
            continuum
        );
    }
    private static WorldContinuumTrajectory ReadContinuum(ref WireReader reader) {
        var previousPosition = reader.ReadFixedVector();
        var sourceTick = reader.ReadUInt64();
        var start = reader.ReadUInt64();
        var end = reader.ReadUInt64();
        var consumedThrough = reader.ReadUInt64();
        var boundaryEvents = reader.ReadByte();

        if (
            !reader.Failed &&
            ((end <= start) || (consumedThrough < end) || (boundaryEvents == 0) || (boundaryEvents > WorldContinuumTrajectory.MaxBoundaryEvents))
        ) {
            reader.Fail(
                detail: $"continuum trajectory has invalid interval [{start},{end}), consumed-through {consumedThrough}, or boundary count {boundaryEvents}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return new WorldContinuumTrajectory(
            BoundaryEvents: boundaryEvents,
            ConsumedThroughEngineTick: consumedThrough,
            ContinuumEndEngineTick: end,
            ContinuumStartEngineTick: start,
            PreviousPosition: previousPosition,
            SourceTick: sourceTick
        );
    }
    private static EntitySnapshot ReadEntity(ref WireReader reader, int ordinal) {
        var index = reader.ReadInt32();
        var position = reader.ReadFiniteVector(field: $"snapshot entity {ordinal} position");
        var orientation = reader.ReadQuaternion();
        var heading = reader.ReadSingle();
        var bodyColor = reader.ReadFiniteVector(field: $"snapshot entity {ordinal} body color");
        var active = reader.ReadBoolean();
        var kit = reader.ReadByte();
        var look = reader.ReadByte();
        var catalogRig = reader.ReadByte();
        var continuityKind = ((EntityContinuityKind)reader.ReadByte());

        if (!Enum.IsDefined(value: continuityKind)) {
            reader.Fail(
                detail: $"snapshot entity {ordinal} continuity kind {((byte)continuityKind)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var seconds = reader.ReadSingle();
        var generation = reader.ReadInt32();
        var placementId = reader.ReadNullableString(field: $"snapshot entity {ordinal} placement id");

        if (
            !reader.Failed &&
            (catalogRig >= WorldLookSource.Catalog.RigCount)
        ) {
            reader.Fail(
                detail: $"snapshot entity {index} catalog rig {catalogRig} is outside 0..{(WorldLookSource.Catalog.RigCount - 1)}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return new EntitySnapshot(
            index,
            position,
            orientation,
            bodyColor,
            active,
            kit,
            look,
            catalogRig,
            new EntityContinuity(
                Kind: continuityKind,
                Seconds: seconds
            ),
            generation,
            placementId,
            heading
        );
    }
    private static IntentSource ReadIntentSource(ref WireReader reader) {
        var tag = reader.ReadByte();

        switch (tag) {
            case 0:
                return IntentSource.Live;
            case 1:
                return IntentSource.Idle;
            case 2:
                return IntentSource.Producer(name: reader.ReadString(field: "intent source producer name"));
            default:
                reader.Fail(
                    detail: $"intent source tag {tag} is not declared",
                    refusal: WireRefusal.EnumValueUnknown
                );

                return IntentSource.Idle;
        }
    }
    private static bool TryDeserializeDefinition(byte[] bytes, string field, out WorldDefinition? definition, out WireFailure failure) {
        try {
            definition = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);
            failure = default;

            return true;
            // WorldDefinitionSerialization.Deserialize reports a bad document as InvalidDataException and a document that
            // parses but fails local validation as InvalidOperationException; both are untrusted-input refusals here.
        } catch (Exception exception) when ((exception is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException)) {
            definition = null;
            failure = new WireFailure(
                Refusal: WireRefusal.PayloadMalformed,
                Detail: $"{field} does not decode — {exception.Message.ReplaceLineEndings(replacementText: " ")}"
            );

            return false;
        }
    }
    private static bool TryReadReservationMember(ref WireReader reader, int ordinal, WorldPlayerDefaults defaults, out WorldTransferReservationMember member, out WireFailure failure) {
        member = default;

        var preferred = reader.ReadInt32();
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);

        if (
            !reader.Failed &&
            (string.IsNullOrWhiteSpace(value: mobility.Incarnation.Authority) || (mobility.Incarnation.Index < 0) || (mobility.Incarnation.Generation <= 0))
        ) {
            reader.Fail(
                detail: $"traveler {(ordinal + 1)} mobility incarnation is invalid",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        var source = ReadIntentSource(reader: ref reader);
        var bodyColor = reader.ReadFiniteVector(field: $"traveler {(ordinal + 1)} body color");
        var catalogRig = reader.ReadByte();
        WorldIdentity? identity = null;

        if (reader.ReadBoolean()) {
            var projection = new WorldIdentityProjection(
                Id: reader.ReadRequiredString(field: $"traveler {(ordinal + 1)} identity id"),
                Name: reader.ReadString(field: $"traveler {(ordinal + 1)} identity name"),
                ColorHex: reader.ReadString(field: $"traveler {(ordinal + 1)} identity color"),
                MoveSpeed: reader.ReadFixed(),
                TurnSpeed: reader.ReadFixed()
            );

            if (!reader.Failed) {
                identity = WorldIdentity.FromProjection(
                    defaults: defaults,
                    projection: in projection
                );
            }
        }

        if (reader.Failed) {
            failure = reader.Failure;

            return false;
        }

        member = new WorldTransferReservationMember(
            Principal: WorldPrincipal.Console,
            PreferredSlot: preferred,
            Identity: identity,
            Source: source,
            BodyColor: bodyColor,
            CatalogRig: catalogRig,
            Mobility: mobility
        );
        failure = default;

        return true;
    }
    private static void WriteCommitMember(WireWriter writer, WorldTransferCommitMember member) {
        writer.WriteBlock(value: ((member.Profile?.Document is { } profileDocument)
            ? WorldDefinitionSerialization.Serialize(definition: profileDocument)
            : []));
        writer.WriteBoolean(value: member.HasMappedArrival);
        writer.WriteString(value: member.BodyMotionProgramName);
        writer.WriteFixedVector(value: member.Position);
        writer.WriteFixed(value: member.YawRadians);
        writer.WriteFixedVector(value: member.PlanarVelocity);
        writer.WriteFixed(value: member.VerticalVelocity);

        var continuity = (member.ActionContinuity ?? new WorldTransferActionContinuity(
            Channels: [],
            Registers: []
        ));

        writer.WriteInt32(value: continuity.Channels.Count);

        foreach (var channel in continuity.Channels) {
            writer.WriteString(value: channel.Name);
            writer.WriteBoolean(value: channel.PreviousBit);
            writer.WriteFixed(value: channel.HeldValue);
        }

        writer.WriteInt32(value: continuity.Registers.Count);

        foreach (var register in continuity.Registers) {
            writer.WriteString(value: register.Name);
            writer.WriteByte(value: ((byte)register.Kind));
            writer.WriteFixed(value: register.Value);
            writer.WriteUInt64(value: register.TimerTicks);
        }

        writer.WriteBoolean(value: member.Continuum.HasValue);

        if (member.Continuum is { } continuum) {
            writer.WriteFixedVector(value: continuum.PreviousPosition);
            writer.WriteUInt64(value: continuum.SourceTick);
            writer.WriteUInt64(value: continuum.ContinuumStartEngineTick);
            writer.WriteUInt64(value: continuum.ContinuumEndEngineTick);
            writer.WriteUInt64(value: continuum.ConsumedThroughEngineTick);
            writer.WriteByte(value: continuum.BoundaryEvents);
        }
    }
    private static void WriteIntentSource(WireWriter writer, IntentSource source) {
        if (source.IsLive) {
            writer.WriteByte(value: 0);
        } else if (source.IsIdle) {
            writer.WriteByte(value: 1);
        } else if (
            source.IsProducer &&
            (source.ProducerName is { } producerName)
        ) {
            writer.WriteByte(value: 2);
            writer.WriteString(value: producerName);
        } else {
            throw new InvalidOperationException(message: $"intent source '{source}' is not defined");
        }
    }
    /// <summary>Encodes the challenge proof sent before every federation operation. Carries no claimed source
    /// namespace — the door derives that from the verified proof itself (<see cref="WorldAttestedAuthenticator"/>),
    /// never from anything the wire asserts.</summary>
    /// <param name="proof">The challenge proof.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeAuthentication(ReadOnlySpan<byte> proof) => proof.ToArray();
    /// <summary>Encodes a cohort commit.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <param name="members">The detached cohort.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeCommit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members) {
        ArgumentNullException.ThrowIfNull(argument: members);

        var writer = new WireWriter(capacity: 4096);

        writer.WriteString(value: sourceAuthority);
        writer.WriteUInt64(value: transferId);
        writer.WriteInt32(value: members.Count);

        foreach (var member in members) {
            WriteCommitMember(
                member: member,
                writer: writer
            );
        }

        return writer.ToArray();
    }
    /// <summary>Encodes a commit verdict.</summary>
    /// <param name="accepted">Whether the commit landed.</param>
    /// <param name="reason">The refusal narration when it did not.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeCommitReply(bool accepted, string reason) {
        var writer = new WireWriter();

        writer.WriteBoolean(value: accepted);
        writer.WriteString(value: reason);

        return writer.ToArray();
    }
    /// <summary>Encodes what <paramref name="tier"/> authorizes a peer to receive of the authority's document: a
    /// <c>puck.world.projection.v1</c> document at <see cref="WorldDisclosureTier.Presentation"/>, the definition
    /// verbatim at <see cref="WorldDisclosureTier.Replica"/>. The leading byte is the tier, so a receiver names what
    /// it was handed rather than inferring it from the content.</summary>
    /// <param name="definition">The authority's live document.</param>
    /// <param name="tier">The tier the admission door decided for this peer.</param>
    /// <param name="authority">The composing authority's addressable namespace.</param>
    /// <param name="revision">The document revision this composition names.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is
    /// <see cref="WorldDisclosureTier.Frames"/>, which carries no document at all.</exception>
    public static byte[] EncodeDocument(WorldDefinition definition, WorldDisclosureTier tier, string authority, int revision) {
        var payload = ((WorldProjection.Compose(
            authority: authority,
            definition: definition,
            revision: revision,
            tier: tier
        ) is { } projection)
            ? WorldProjection.Serialize(projection: projection)
            : ((tier == WorldDisclosureTier.Replica)
                ? WorldDefinitionSerialization.Serialize(definition: definition)
                : throw new ArgumentOutOfRangeException(
                    paramName: nameof(tier),
                    actualValue: tier,
                    message: "a frames-tier peer receives no document"
                )
        ));
        var bytes = new byte[(payload.Length + 1)];

        bytes[0] = ((byte)tier);
        payload.CopyTo(
            array: bytes,
            index: 1
        );

        return bytes;
    }
    /// <summary>Encodes a transferred body's per-tick intent with its lease credential.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="submission">The intent image.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeIntent(string sourceAuthority, in WorldMobilityIdentity mobility, in IntentSubmission submission) {
        var writer = new WireWriter(capacity: 1024);

        writer.WriteString(value: sourceAuthority);
        WorldWireLeaves.WriteMobility(
            mobility: mobility,
            writer: writer
        );
        writer.WriteUInt64(value: submission.Tick);
        writer.WriteInt32(value: submission.MeasuredHoldTicks);

        for (var channel = 0; (channel < ChannelLimits.MaxChannels); channel++) {
            writer.WriteFixed(value: submission.Intent[channel]);
        }

        for (var channel = 0; (channel < ChannelLimits.MaxChannels); channel++) {
            writer.WriteFixed(value: submission.HeldChannels[channel]);
        }

        return writer.ToArray();
    }
    /// <summary>Encodes a reservation including each traveler's identity projection — appearance and motion-envelope
    /// claims alone, never the owned-world document behind them (see <see cref="WorldIdentityProjection"/>).</summary>
    /// <param name="request">The reservation request.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A traveler carries no mobility identity.</exception>
    public static byte[] EncodeReservation(WorldTransferReservationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        var writer = new WireWriter(capacity: 8192);

        writer.WriteUInt64(value: request.TransferId);
        writer.WriteString(value: request.SourceAuthority);
        writer.WriteInt32(value: request.SourceRateHz);
        writer.WriteUInt64(value: request.SourceTick);
        writer.WriteUInt64(value: request.DeadlineSourceTick);
        writer.WriteString(value: request.Border);
        writer.WriteBoolean(value: request.BorderCapacity.HasValue);

        if (request.BorderCapacity is { } capacity) {
            writer.WriteInt32(value: capacity);
        }

        writer.WriteBoolean(value: request.PartyAllOrNothing);
        // A wire reservation always requests entity-table peer admission.
        writer.WriteBoolean(value: true);
        writer.WriteInt32(value: request.Members.Count);

        foreach (var member in request.Members) {
            writer.WriteInt32(value: member.PreferredSlot);

            if (member.Mobility is not { } mobility) {
                throw new InvalidOperationException(message: "federated reservation traveler has no mobility identity");
            }

            WorldWireLeaves.WriteMobility(
                mobility: mobility,
                writer: writer
            );
            WriteIntentSource(
                writer: writer,
                source: member.Source
            );
            writer.WriteVector(value: member.BodyColor);
            writer.WriteByte(value: member.CatalogRig);
            writer.WriteBoolean(value: (member.Identity is not null));

            if (member.Identity is { } identity) {
                var projected = identity.Project();

                writer.WriteString(value: projected.Id);
                writer.WriteString(value: projected.Name);
                writer.WriteString(value: projected.ColorHex);
                writer.WriteFixed(value: projected.MoveSpeed);
                writer.WriteFixed(value: projected.TurnSpeed);
            }
        }

        return writer.ToArray();
    }
    /// <summary>Encodes a reservation verdict, disclosing the destination document at <paramref name="tier"/>.</summary>
    /// <param name="reply">The verdict.</param>
    /// <param name="tier">The tier the admission door decided for the reserving peer.</param>
    /// <param name="authority">The composing authority's addressable namespace.</param>
    /// <param name="revision">The document revision this composition names.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reply"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeReservationReply(WorldTransferReservationReply reply, WorldDisclosureTier tier, string authority, int revision) {
        ArgumentNullException.ThrowIfNull(argument: reply);

        var writer = new WireWriter(capacity: 4096);

        writer.WriteBoolean(value: reply.Accepted);
        writer.WriteString(value: reply.Reason);
        writer.WriteUInt64(value: reply.DeadlineDestinationTick);
        writer.WriteInt32(value: reply.BodyIndices.Count);

        foreach (var slot in reply.BodyIndices) {
            writer.WriteInt32(value: slot);
        }

        writer.WriteBlock(value: ((reply.DestinationDefinition is { } definition)
            ? EncodeDocument(
                authority: authority,
                definition: definition,
                revision: revision,
                tier: tier
            )
            : []));

        return writer.ToArray();
    }
    /// <summary>Encodes the final observable authority epoch for one traveler, disclosing the carried document at
    /// <paramref name="tier"/>.</summary>
    /// <param name="route">The route description.</param>
    /// <param name="tier">The tier the admission door decided for the asking peer.</param>
    /// <param name="authority">The composing authority's addressable namespace.</param>
    /// <param name="revision">The document revision this composition names.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeRoute(in WorldAuthorityRouteDescription route, WorldDisclosureTier tier, string authority, int revision) {
        var writer = new WireWriter(capacity: 4096);

        writer.WriteString(value: route.Endpoint);
        WorldWireLeaves.WriteEntityAddress(
            writer: writer,
            address: route.Entity
        );
        writer.WriteUInt64(value: route.Tick);
        writer.WriteFixedVector(value: route.Position);
        writer.WriteFixedQuaternion(value: route.Orientation);
        writer.WriteVector(value: route.BodyColor);
        writer.WriteByte(value: route.Kit);
        writer.WriteByte(value: route.Look);
        writer.WriteByte(value: route.CatalogRig);
        writer.WriteNullableString(value: route.PlacementId);
        writer.WriteBlock(value: EncodeDocument(
            definition: route.Definition,
            tier: tier,
            authority: authority,
            revision: revision
        ));

        return writer.ToArray();
    }
    /// <summary>Encodes a committed-transfer credential without a gameplay payload.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="mobility">The traveler's incarnation and committed epoch.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeRouteCredential(string sourceAuthority, in WorldMobilityIdentity mobility) {
        var writer = new WireWriter();

        writer.WriteString(value: sourceAuthority);
        WorldWireLeaves.WriteMobility(
            mobility: mobility,
            writer: writer
        );

        return writer.ToArray();
    }
    /// <summary>Encodes one per-tick projection record.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeSnapshot(in WorldSnapshot snapshot) {
        var writer = new WireWriter(capacity: (256 + (snapshot.Entries.Length * 64)));

        writer.WriteUInt64(value: snapshot.Tick);
        writer.WriteInt32(value: snapshot.Revision);
        writer.WriteUInt64(value: snapshot.StepTicks);
        writer.WriteString(value: snapshot.Authority);
        writer.WriteInt32(value: snapshot.Entries.Length);

        foreach (var entry in snapshot.Entries.Span) {
            writer.WriteInt32(value: entry.Index);
            writer.WriteVector(value: entry.Position);
            writer.WriteQuaternion(value: entry.Orientation);
            writer.WriteSingle(value: entry.Heading);
            writer.WriteVector(value: entry.BodyColor);
            writer.WriteBoolean(value: entry.Active);
            writer.WriteByte(value: entry.Kit);
            writer.WriteByte(value: entry.Look);
            writer.WriteByte(value: entry.CatalogRig);
            writer.WriteByte(value: ((byte)entry.Continuity.Kind));
            writer.WriteSingle(value: entry.Continuity.Seconds);
            writer.WriteInt32(value: entry.Generation);
            writer.WriteNullableString(value: entry.PlacementId);
        }

        writer.WriteBoolean(value: snapshot.FieldsFull);
        writer.WriteInt32(value: snapshot.FieldCells.Length);

        foreach (var delta in snapshot.FieldCells.Span) {
            writer.WriteInt32(value: delta.Cell);
            writer.WriteByte(value: delta.Field);
            writer.WriteInt64(value: delta.Raw);
        }

        return writer.ToArray();
    }
    /// <summary>Encodes one forwarded submission frame behind its traveler credential.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="frame">The canonical <see cref="Puck.World.Protocol.WorldFrameCodec"/> frame.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeSubmission(string sourceAuthority, in WorldMobilityIdentity mobility, ReadOnlySpan<byte> frame) {
        var writer = new WireWriter(capacity: (frame.Length + 256));

        writer.WriteString(value: sourceAuthority);
        WorldWireLeaves.WriteMobility(
            mobility: mobility,
            writer: writer
        );
        writer.WriteBytes(value: frame);

        return writer.ToArray();
    }
    /// <summary>Encodes a transfer key.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="transferId">The transfer id inside that namespace.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeTransferKey(string sourceAuthority, ulong transferId) {
        var writer = new WireWriter();

        writer.WriteString(value: sourceAuthority);
        writer.WriteUInt64(value: transferId);

        return writer.ToArray();
    }
    /// <summary>Returns the hard body cap for a declared request kind.</summary>
    /// <param name="kind">The request kind.</param>
    /// <returns>The maximum body bytes accepted.</returns>
    public static int MaxRequestBytes(WorldFederationRequest kind) => kind switch {
        WorldFederationRequest.Observe => 0,
        WorldFederationRequest.IntentStream => 0,
        WorldFederationRequest.IntentStreamHandoff => 0,
        WorldFederationRequest.Authenticate => MaxProofBytes,
        WorldFederationRequest.Abort => (2 * WireLimits.MaxStringBytes),
        WorldFederationRequest.Status => (2 * WireLimits.MaxStringBytes),
        WorldFederationRequest.AcknowledgeTransfer => (2 * WireLimits.MaxStringBytes),
        WorldFederationRequest.Route => (4 * WireLimits.MaxStringBytes),
        WorldFederationRequest.Intent => (64 * 1024),
        WorldFederationRequest.Submission => (WireLimits.MaxDocumentBytes + (64 * 1024)),
        WorldFederationRequest.Reserve => MaxFrameBytes,
        WorldFederationRequest.Commit => ((4 * 1024) * 1024),
        _ => 0,
    };
    /// <summary>Returns the hard body cap for a declared response kind.</summary>
    /// <param name="kind">The response kind.</param>
    /// <returns>The maximum body bytes accepted.</returns>
    public static int MaxResponseBytes(WorldFederationResponse kind) => kind switch {
        WorldFederationResponse.Ack => 0,
        WorldFederationResponse.Status => sizeof(byte),
        WorldFederationResponse.Challenge => MaxProofBytes,
        WorldFederationResponse.Refusal => WireLimits.MaxStringBytes,
        WorldFederationResponse.Commit => (2 * WireLimits.MaxStringBytes),
        WorldFederationResponse.Snapshot => ((4 * 1024) * 1024),
        WorldFederationResponse.Completion => (1024 * 1024),
        WorldFederationResponse.Definition => WireLimits.MaxDocumentBytes,
        WorldFederationResponse.Reservation => MaxFrameBytes,
        WorldFederationResponse.Route => MaxFrameBytes,
        _ => 0,
    };
    /// <summary>Reads one framed request, refusing an undeclared kind or an over-cap body by name.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The frame, or a named refusal.</returns>
    public static async Task<WireFrameRead> ReadRequestAsync(Stream stream, CancellationToken ct) {
        var read = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: MaxFrameBytes,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!read.Ok) {
            return read;
        }

        var kind = ((WorldFederationRequest)read.Kind);

        if (!Enum.IsDefined(value: kind)) {
            return WireFrameRead.Refused(
                refusal: WireRefusal.FrameKindUnknown,
                detail: $"federation request kind {read.Kind} is not declared"
            );
        }

        var cap = MaxRequestBytes(kind: kind);

        return ((read.Body.Length > cap)
            ? WireFrameRead.Refused(
                refusal: WireRefusal.PayloadTooLarge,
                detail: $"{kind} body is {read.Body.Length} bytes; cap is {cap}"
            )
            : read
        );
    }
    /// <summary>Reads one framed response, refusing an undeclared kind or an over-cap body by name.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The frame, or a named refusal.</returns>
    public static async Task<WireFrameRead> ReadResponseAsync(Stream stream, CancellationToken ct) {
        var read = await WireFrame.ReadAsync(
            ct: ct,
            maxFrameBytes: MaxFrameBytes,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!read.Ok) {
            return read;
        }

        var kind = ((WorldFederationResponse)read.Kind);

        if (!Enum.IsDefined(value: kind)) {
            return WireFrameRead.Refused(
                refusal: WireRefusal.FrameKindUnknown,
                detail: $"federation response kind {read.Kind} is not declared"
            );
        }

        var cap = MaxResponseBytes(kind: kind);

        return ((read.Body.Length > cap)
            ? WireFrameRead.Refused(
                refusal: WireRefusal.PayloadTooLarge,
                detail: $"{kind} body is {read.Body.Length} bytes; cap is {cap}"
            )
            : read
        );
    }
    /// <summary>Decodes an authentication leaf. The proof's own shape is <see cref="Puck.Networking.IAuthenticator.TryVerify"/>'s
    /// call to make, never this decode's — a fixed length here would bind the wire to one authenticator scheme.
    /// Carries no source-authority field: the leaf IS the proof, and the identity it establishes comes back only
    /// from a successful verify, never from a claimed label alongside it.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="proof">The presented proof.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeAuthentication(ReadOnlySpan<byte> body, out byte[] proof, out WireFailure failure) {
        if (body.Length > MaxProofBytes) {
            proof = [];
            failure = new WireFailure(
                Detail: $"authentication proof is {body.Length} bytes; cap is {MaxProofBytes}",
                Refusal: WireRefusal.PayloadTooLarge
            );

            return false;
        }

        proof = body.ToArray();
        failure = default;

        return true;
    }
    /// <summary>Decodes a cohort commit.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="sourceAuthority">The claimed source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <param name="defaults">The destination's own player defaults, applied to each carried identity document.</param>
    /// <param name="members">The landing cohort.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeCommit(ReadOnlySpan<byte> body, WorldPlayerDefaults defaults, out string sourceAuthority, out ulong transferId, out WorldTransferCommitMember[] members, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        sourceAuthority = reader.ReadRequiredString(field: "commit source authority");
        transferId = reader.ReadUInt64();
        members = [];

        var count = reader.ReadCount(
            field: "commit member count",
            maximum: WorldBodiesLimits.CapacityCeiling,
            minimum: 1
        );

        if (reader.Failed) {
            return Finish(
                failure: out failure,
                reader: ref reader
            );
        }

        members = new WorldTransferCommitMember[count];

        for (var index = 0; (index < count); index++) {
            members[index] = ReadCommitMember(
                defaults: defaults,
                ordinal: index,
                reader: ref reader
            );
        }

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes a commit verdict.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="accepted">Whether the commit landed.</param>
    /// <param name="reason">The refusal narration.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeCommitReply(ReadOnlySpan<byte> body, out bool accepted, out string reason, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        accepted = reader.ReadBoolean();
        reason = reader.ReadString(field: "commit reply reason");

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes a tier-tagged document leaf, hydrating a projection into a locally-valid definition.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="definition">The definition on success.</param>
    /// <param name="tier">The tier the leaf declared.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeDocument(ReadOnlySpan<byte> body, out WorldDefinition? definition, out WorldDisclosureTier tier, out WireFailure failure) {
        definition = null;
        tier = WorldDisclosureTier.Frames;

        if (body.Length < 1) {
            failure = new WireFailure(
                Detail: "a document leaf carries no disclosure tier byte",
                Refusal: WireRefusal.PayloadTruncated
            );

            return false;
        }

        if (body.Length > (WireLimits.MaxDocumentBytes + 1)) {
            failure = new WireFailure(
                Refusal: WireRefusal.PayloadTooLarge,
                Detail: $"document is {body.Length} bytes; cap is {(WireLimits.MaxDocumentBytes + 1)}"
            );

            return false;
        }

        tier = ((WorldDisclosureTier)body[0]);

        if (!Enum.IsDefined(value: tier)) {
            failure = new WireFailure(
                Refusal: WireRefusal.EnumValueUnknown,
                Detail: $"document disclosure tier {body[0]} is not declared"
            );

            return false;
        }

        var payload = body[1..];

        if (tier == WorldDisclosureTier.Replica) {
            return TryDeserializeDefinition(
                bytes: payload.ToArray(),
                field: "definition",
                definition: out definition,
                failure: out failure
            );
        }

        if (tier == WorldDisclosureTier.Frames) {
            failure = new WireFailure(
                Detail: "a frames-tier leaf carries a document body",
                Refusal: WireRefusal.PayloadMalformed
            );

            return false;
        }

        if (
            !WorldProjection.TryDeserialize(
            projection: out var projection,
            reason: out var reason,
            utf8Json: payload
        ) ||
            (projection is null)
        ) {
            failure = new WireFailure(
                Detail: reason,
                Refusal: WireRefusal.PayloadMalformed
            );

            return false;
        }

        if (!WorldProjection.TryToDefinition(
            projection: projection,
            definition: out definition,
            reason: out var hydrationReason
        )) {
            failure = new WireFailure(
                Detail: hydrationReason,
                Refusal: WireRefusal.PayloadMalformed
            );

            return false;
        }

        failure = default;

        return true;
    }
    /// <summary>Decodes a transferred body's per-tick intent.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="sourceAuthority">The claimed source namespace.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="submission">The intent image.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeIntent(ReadOnlySpan<byte> body, out string sourceAuthority, out WorldMobilityIdentity mobility, out IntentSubmission submission, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        sourceAuthority = reader.ReadRequiredString(field: "intent source authority");
        mobility = WorldWireLeaves.ReadMobility(reader: ref reader);

        var tick = reader.ReadUInt64();
        var measured = reader.ReadInt32();
        var intent = default(PlayerIntent);
        var held = default(PlayerIntent);

        for (var channel = 0; (channel < ChannelLimits.MaxChannels); channel++) {
            intent = intent.WithChannel(
                ordinal: channel,
                value: reader.ReadFixed()
            );
        }

        for (var channel = 0; (channel < ChannelLimits.MaxChannels); channel++) {
            held = held.WithChannel(
                ordinal: channel,
                value: reader.ReadFixed()
            );
        }

        submission = new IntentSubmission(
            tick,
            -1,
            intent,
            WorldPrincipal.Console,
            held,
            measured
        );

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes an untrusted reservation, rebuilding identities through the canonical document codec.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="defaults">The destination's player defaults, applied to each rebuilt identity.</param>
    /// <param name="request">The reservation on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeReservation(ReadOnlySpan<byte> body, WorldPlayerDefaults defaults, out WorldTransferReservationRequest? request, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        request = null;

        var transferId = reader.ReadUInt64();
        var sourceAuthority = reader.ReadRequiredString(field: "reservation source authority");
        var sourceRate = reader.ReadInt32();
        var sourceTick = reader.ReadUInt64();
        var deadline = reader.ReadUInt64();
        var border = reader.ReadString(field: "reservation border");
        int? capacity = (reader.ReadBoolean()
            ? reader.ReadInt32()
            : null
        );
        var party = reader.ReadBoolean();
        var remote = reader.ReadBoolean();
        var count = reader.ReadCount(
            field: "reservation traveler count",
            maximum: WorldBodiesLimits.CapacityCeiling,
            minimum: 1
        );

        if (reader.Failed) {
            return Finish(
                failure: out failure,
                reader: ref reader
            );
        }

        var members = new WorldTransferReservationMember[count];

        for (var index = 0; (index < count); index++) {
            if (!TryReadReservationMember(
                reader: ref reader,
                ordinal: index,
                defaults: defaults,
                member: out members[index],
                failure: out failure
            )) {
                return false;
            }
        }

        request = new WorldTransferReservationRequest(
            Border: border,
            BorderCapacity: capacity,
            DeadlineSourceTick: deadline,
            Members: members,
            PartyAllOrNothing: party,
            PeerAdmission: remote,
            SourceAuthority: sourceAuthority,
            SourceRateHz: sourceRate,
            SourceTick: sourceTick,
            TransferId: transferId
        );

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes a reservation verdict.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="reply">The verdict on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeReservationReply(ReadOnlySpan<byte> body, out WorldTransferReservationReply? reply, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        reply = null;

        var accepted = reader.ReadBoolean();
        var reason = reader.ReadString(field: "reservation reply reason");
        var deadline = reader.ReadUInt64();
        var count = reader.ReadCount(
            field: "reservation reply body count",
            maximum: WorldBodiesLimits.CapacityCeiling,
            minimum: 0
        );

        if (reader.Failed) {
            return Finish(
                failure: out failure,
                reader: ref reader
            );
        }

        var slots = new int[count];

        for (var index = 0; (index < count); index++) {
            slots[index] = reader.ReadInt32();
        }

        var definitionBytes = reader.ReadBlock(
            field: "reservation reply document",
            maxBytes: (WireLimits.MaxDocumentBytes + 1)
        );

        if (reader.Failed) {
            return Finish(
                failure: out failure,
                reader: ref reader
            );
        }

        WorldDefinition? definition = null;

        if (
            (definitionBytes.Length > 0) &&
            !TryDecodeDocument(
            body: definitionBytes,
            definition: out definition,
            failure: out failure,
            tier: out _
        )
        ) {
            return false;
        }

        reply = new WorldTransferReservationReply(
            Accepted: accepted,
            BodyIndices: slots,
            DeadlineDestinationTick: deadline,
            DestinationDefinition: definition,
            Reason: reason
        );

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes a route description and checks it against the definition it carries.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="route">The route on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeRoute(ReadOnlySpan<byte> body, out WorldAuthorityRouteDescription route, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        route = default;

        var endpoint = reader.ReadRequiredString(field: "route endpoint");
        var entity = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
        var tick = reader.ReadUInt64();
        var position = reader.ReadFixedVector();
        var orientation = reader.ReadFixedQuaternion();
        var bodyColor = reader.ReadFiniteVector(field: "route body color");
        var kit = reader.ReadByte();
        var look = reader.ReadByte();
        var catalogRig = reader.ReadByte();
        var placementId = reader.ReadNullableString(field: "route placement id");
        var definitionBytes = reader.ReadBlock(
            field: "route document",
            maxBytes: (WireLimits.MaxDocumentBytes + 1)
        );

        if (reader.Failed) {
            return Finish(
                failure: out failure,
                reader: ref reader
            );
        }

        if (
            !TryDecodeDocument(
            body: definitionBytes,
            definition: out var definition,
            failure: out failure,
            tier: out _
        ) ||
            (definition is null)
        ) {
            return false;
        }

        if (
            (((uint)entity.Index) >= ((uint)WorldBodiesLimits.CapacityCeiling)) ||
            (entity.Generation < 0)
        ) {
            reader.Fail(
                detail: $"route entity {entity.Index}/{entity.Generation} is outside the admitted population address space",
                refusal: WireRefusal.PayloadMalformed
            );
        } else if (
            (kit >= definition.Kits.Count) ||
            (look >= Math.Max(
            val1: definition.Looks.Count,
            val2: 1
        )) ||
            (catalogRig >= WorldLookSource.Catalog.RigCount)
        ) {
            reader.Fail(
                detail: $"route kit/look/rig {kit}/{look}/{catalogRig} is outside the carried definition's authored sets",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        route = new WorldAuthorityRouteDescription(
            BodyColor: bodyColor,
            CatalogRig: catalogRig,
            Definition: definition,
            Endpoint: endpoint,
            Entity: entity,
            Kit: kit,
            Look: look,
            Orientation: orientation,
            PlacementId: placementId,
            Position: position,
            Tick: tick
        );

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes a committed-transfer credential.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="sourceAuthority">The claimed source namespace.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeRouteCredential(ReadOnlySpan<byte> body, out string sourceAuthority, out WorldMobilityIdentity mobility, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        sourceAuthority = reader.ReadRequiredString(field: "route credential source authority");
        mobility = WorldWireLeaves.ReadMobility(reader: ref reader);

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes one per-tick projection record. The entry count is bounded by the admitted population
    /// ceiling before any entry array is allocated for it.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="snapshot">The snapshot on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeSnapshot(ReadOnlySpan<byte> body, out WorldSnapshot snapshot, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        var tick = reader.ReadUInt64();
        var revision = reader.ReadInt32();
        var stepTicks = reader.ReadUInt64();
        var authority = reader.ReadString(field: "snapshot authority");
        var count = reader.ReadCount(
            field: "snapshot entry count",
            maximum: WorldBodiesLimits.CapacityCeiling,
            minimum: 0
        );

        if (reader.Failed) {
            snapshot = default;

            return Finish(
                failure: out failure,
                reader: ref reader
            );
        }

        var entries = new EntitySnapshot[count];

        for (var index = 0; (index < count); index++) {
            entries[index] = ReadEntity(
                ordinal: index,
                reader: ref reader
            );
        }

        var fieldsFull = reader.ReadBoolean();
        var deltaCount = reader.ReadCount(
            field: "snapshot field cell count",
            maximum: (WorldFieldCapacity.MaxFields * WorldFieldCapacity.MaxCells),
            minimum: 0
        );

        if (reader.Failed) {
            snapshot = default;

            return Finish(
                failure: out failure,
                reader: ref reader
            );
        }

        var deltas = new FieldCellDelta[deltaCount];

        for (var index = 0; (index < deltaCount); index++) {
            var cell = reader.ReadInt32();
            var field = reader.ReadByte();
            var raw = reader.ReadInt64();

            deltas[index] = new FieldCellDelta(
                Cell: cell,
                Field: field,
                Raw: raw
            );
        }

        snapshot = new WorldSnapshot(
            Authority: authority,
            Entries: entries,
            Revision: revision,
            StepTicks: stepTicks,
            Tick: tick,
            FieldCells: deltas,
            FieldsFull: fieldsFull
        );

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes one forwarded submission frame.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="sourceAuthority">The claimed source namespace.</param>
    /// <param name="mobility">The traveler credential.</param>
    /// <param name="frame">The carried submission frame.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeSubmission(ReadOnlySpan<byte> body, out string sourceAuthority, out WorldMobilityIdentity mobility, out byte[] frame, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        sourceAuthority = reader.ReadRequiredString(field: "submission source authority");
        mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        frame = reader.ReadRest(
            field: "submission frame",
            maxBytes: WorldTcpWireFormat.MaxUpstreamFrameBytes
        );

        if (
            !reader.Failed &&
            (frame.Length < Puck.Networking.FrameCodec.PrefixBytes)
        ) {
            reader.Fail(
                refusal: WireRefusal.PayloadTruncated,
                detail: $"submission frame is {frame.Length} bytes; at least {Puck.Networking.FrameCodec.PrefixBytes} are required"
            );
        }

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Decodes a transfer key.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="sourceAuthority">The claimed source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeTransferKey(ReadOnlySpan<byte> body, out string sourceAuthority, out ulong transferId, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        sourceAuthority = reader.ReadRequiredString(field: "transfer key source authority");
        transferId = reader.ReadUInt64();

        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Writes the federation discriminator that opens every connection. This is the only hello: the
    /// challenge/authenticate exchange that follows rides ordinary frames.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The write task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static async Task WriteHelloAsync(Stream stream, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(argument: stream);

        var bytes = new byte[sizeof(ulong)];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            destination: bytes,
            value: WireKey
        );

        await stream.WriteAsync(
            buffer: bytes,
            cancellationToken: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Writes a named refusal frame.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="refusal">The stable refusal name.</param>
    /// <param name="detail">The refusal narration.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The write task.</returns>
    public static Task WriteRefusalAsync(Stream stream, WorldFederationRefusal refusal, string detail, CancellationToken ct) =>
        WriteResponseAsync(
            stream: stream,
            kind: WorldFederationResponse.Refusal,
            body: Encoding.UTF8.GetBytes(s: $"{refusal}: {detail}"),
            ct: ct
        );
    /// <summary>Writes one framed request.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="kind">The request kind.</param>
    /// <param name="body">The encoded leaf.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The write task.</returns>
    public static Task WriteRequestAsync(Stream stream, WorldFederationRequest kind, ReadOnlyMemory<byte> body, CancellationToken ct) =>
        WireFrame.WriteAsync(
            body: body,
            ct: ct,
            kind: ((byte)kind),
            stream: stream
        );
    /// <summary>Writes one framed response.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="kind">The response kind.</param>
    /// <param name="body">The encoded leaf.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The write task.</returns>
    public static Task WriteResponseAsync(Stream stream, WorldFederationResponse kind, ReadOnlyMemory<byte> body, CancellationToken ct) =>
        WireFrame.WriteAsync(
            body: body,
            ct: ct,
            kind: ((byte)kind),
            stream: stream
        );
}
