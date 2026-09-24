using Puck.Commands;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.Networking;

namespace Puck.World.Protocol;

/// <summary>The declared wire discriminants for the submission payload leaves.</summary>
public enum WorldSubmissionKind : byte {
    /// <summary>An authority command.</summary>
    Command = 1,
    /// <summary>A grant acquisition.</summary>
    Grant = 2,
    /// <summary>A grant revocation.</summary>
    Revoke = 3,
    /// <summary>A session request.</summary>
    Session = 4,
    /// <summary>A whole-document rebuild-and-swap request (reset/load/reload).</summary>
    Rebuild = 5,
    /// <summary>A document mutation.</summary>
    Mutation = 6,
    /// <summary>A journal undo count.</summary>
    Undo = 7,
    /// <summary>A live composition write.</summary>
    Composition = 8,
    /// <summary>A live presentation lever.</summary>
    Lever = 9,
    /// <summary>A read-back query.</summary>
    Query = 10,
    /// <summary>A live screen-machine lifecycle change (insert/eject/select/options/link/unlink).</summary>
    ScreenOp = 11,
    /// <summary>A subject-bearing target-register write.</summary>
    Designation = 12,
    /// <summary>A generic operation on one named machine instance.</summary>
    Operation = 13,
}
/// <summary>A stable leaf/frame codec refusal. Both encoder and decoder return these by name; neither treats malformed
/// caller state or untrusted bytes as an invariant exception.</summary>
public enum WorldCodecRefusal : byte {
    /// <summary>The caller supplied no payload.</summary>
    PayloadMissing,
    /// <summary>The closed payload union has no declared wire kind for this value.</summary>
    PayloadKindUnknown,
    /// <summary>A closed nested union has no declared discriminant for this value.</summary>
    LeafKindUnknown,
    /// <summary>A principal does not have the canonical shape required by its kind.</summary>
    PrincipalShapeInvalid,
    /// <summary>An enum lane carries no declared wire value.</summary>
    EnumValueUnknown,
    /// <summary>The payload bytes are truncated.</summary>
    PayloadTruncated,
    /// <summary>The payload contains bytes after its canonical leaf.</summary>
    PayloadTrailingBytes,
    /// <summary>The payload is structurally malformed.</summary>
    PayloadMalformed,
    /// <summary>The payload exceeds its kind's hard frame cap.</summary>
    PayloadTooLarge,
    /// <summary>The frame length is missing, impossible, or disagrees with the supplied bytes.</summary>
    FrameLengthInvalid,
    /// <summary>The frame's kind byte is not declared.</summary>
    FrameKindUnknown,
}
/// <summary>One named codec refusal plus narration suitable for a console/error frame.</summary>
/// <param name="Refusal">The stable refusal name.</param>
/// <param name="Detail">The human-readable detail.</param>
public readonly record struct WorldCodecFailure(WorldCodecRefusal Refusal, string Detail) {
    /// <summary>Formats the stable name beside its detail.</summary>
    /// <returns>The refusal narration.</returns>
    public override string ToString() => $"{Refusal}: {Detail}";
}
/// <summary>
/// The one canonical encoder/decoder pair for each declared <see cref="WorldSubmissionPayload"/> leaf. The
/// wire framer, loopback, and replay tape all call these methods; none owns a second command/grant vocabulary.
/// </summary>
public static class WorldSubmissionCodec {
    private const int MaxDurableStateValues = 256;

    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private static byte CompositionKind(WorldComposition value) => value switch {
        WorldComposition.SetActiveLayout => 0,
        WorldComposition.SelectCamera => 1,
        _ => throw UnknownLeaf(value: value),
    };
    private static Type? CompositionType(byte kind) => kind switch {
        0 => typeof(WorldComposition.SetActiveLayout),
        1 => typeof(WorldComposition.SelectCamera),
        _ => null,
    };
    private static JsonSerializerOptions CreateJsonOptions() {
        // Puck.World.Protocol already carries the repository's documented reflection-serialization debt. Clone the strict
        // world options so protocol records use the identical converters/member policy, then give concrete union record
        // types a resolver without teaching the document context 72 protocol-only accessors.
        return new JsonSerializerOptions(options: WorldJsonContext.Default.Options) {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
            WorldJsonContext.Default,
            new DefaultJsonTypeInfoResolver()
        ),
            WriteIndented = false,
        };
    }
    private static WorldCodecFailure Fail(WorldCodecRefusal refusal, string detail) => new(
        Detail: detail,
        Refusal: refusal
    );
    private static Type? MutationType(byte kind) {
        try {
            foreach (var entry in WorldMutationKindCatalog.All()) {
                if (entry.Ordinal == kind) {
                    return entry.Type;
                }
            }
        } catch (InvalidOperationException) {
            return null;
        }
        return null;
    }
    private static byte QueryKind(WorldQuery value) => value switch {
        WorldQuery.PlayerWhere => 0,
        WorldQuery.PlayerChannels => 1,
        WorldQuery.WorldPlayers => 2,
        WorldQuery.ScreenState => 3,
        WorldQuery.InputHolds => 4,
        WorldQuery.PlayerState => 5,
        WorldQuery.PlayerTargets => 6,
        WorldQuery.Rules => 7,
        WorldQuery.Properties => 8,
        WorldQuery.Interactions => 9,
        WorldQuery.Contacts => 10,
        WorldQuery.GrantAllows => 11,
        WorldQuery.GrantHandleMint => 12,
        WorldQuery.GrantHandleResolve => 13,
        WorldQuery.PopulationChannels => 14,
        WorldQuery.ProfileCatalog => 15,
        WorldQuery.FindProfile => 16,
        WorldQuery.PreferredControllerProfile => 17,
        WorldQuery.MusicState => 18,
        WorldQuery.InstrumentState => 19,
        WorldQuery.StateObservations => 20,
        WorldQuery.ReflowPreview => 21,
        WorldQuery.ReflowStatus => 22,
        WorldQuery.ReflowCancel => 23,
        _ => throw UnknownLeaf(value: value),
    };
    private static Type? QueryType(byte kind) => kind switch {
        0 => typeof(WorldQuery.PlayerWhere),
        1 => typeof(WorldQuery.PlayerChannels),
        2 => typeof(WorldQuery.WorldPlayers),
        3 => typeof(WorldQuery.ScreenState),
        4 => typeof(WorldQuery.InputHolds),
        5 => typeof(WorldQuery.PlayerState),
        6 => typeof(WorldQuery.PlayerTargets),
        7 => typeof(WorldQuery.Rules),
        8 => typeof(WorldQuery.Properties),
        9 => typeof(WorldQuery.Interactions),
        10 => typeof(WorldQuery.Contacts),
        11 => typeof(WorldQuery.GrantAllows),
        12 => typeof(WorldQuery.GrantHandleMint),
        13 => typeof(WorldQuery.GrantHandleResolve),
        14 => typeof(WorldQuery.PopulationChannels),
        15 => typeof(WorldQuery.ProfileCatalog),
        16 => typeof(WorldQuery.FindProfile),
        17 => typeof(WorldQuery.PreferredControllerProfile),
        18 => typeof(WorldQuery.MusicState),
        19 => typeof(WorldQuery.InstrumentState),
        20 => typeof(WorldQuery.StateObservations),
        21 => typeof(WorldQuery.ReflowPreview),
        22 => typeof(WorldQuery.ReflowStatus),
        23 => typeof(WorldQuery.ReflowCancel),
        _ => null,
    };
    private static WorldCommand ReadCommand(ref WireReader reader) {
        var principal = ReadPrincipal(reader: ref reader);
        var entity = reader.ReadInt32();
        var discriminant = reader.ReadByte();

        switch (discriminant) {
            case 0:
                return ReadSnapPose(
                    entity: entity,
                    principal: principal,
                    reader: ref reader
                );
            case 1: {
                    var intent = WorldWireCodec.ReadIntent(reader: ref reader);
                    var seconds = reader.ReadSingle();

                    return new WorldCommand.EnqueueSegment(
                        EntityIndex: entity,
                        Intent: intent,
                        Principal: principal,
                        Seconds: seconds
                    );
                }
            case 2: {
                    var channelOrdinal = reader.ReadInt32();
                    var value = reader.ReadFixed();
                    var holdSeconds = reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadSingle());

                    return new WorldCommand.PressChannel(
                        ChannelOrdinal: channelOrdinal,
                        EntityIndex: entity,
                        HoldSeconds: holdSeconds,
                        Principal: principal,
                        Value: value
                    );
                }
            case 3:
                return new WorldCommand.SetBodyMotion(
                    BodyMotionProgram: reader.ReadString(field: "SetBodyMotion.BodyMotionProgram"),
                    EntityIndex: entity,
                    Principal: principal
                );
            case 4:
                return new WorldCommand.SetControl(
                    EntityIndex: entity,
                    Principal: principal,
                    Source: WorldWireCodec.ReadIntentSource(reader: ref reader)
                );
            case 5: {
                    var x = reader.ReadSingle();
                    var z = reader.ReadSingle();
                    var yaw = reader.ReadSingle();
                    var seconds = reader.ReadSingle();

                    return new WorldCommand.Reconcile(
                        EntityIndex: entity,
                        Principal: principal,
                        Seconds: seconds,
                        X: x,
                        YawRadians: yaw,
                        Z: z
                    );
                }
            case 6:
                return new WorldCommand.Stop(
                    EntityIndex: entity,
                    Principal: principal
                );
            case 7: {
                    var target = WorldWireCodec.ReadSubject(reader: ref reader);
                    var exclusive = reader.ReadBoolean();
                    var targetPrincipal = ReadPrincipal(reader: ref reader);

                    return new WorldCommand.ComposeControl(
                        EntityIndex: entity,
                        Exclusive: exclusive,
                        Principal: principal,
                        Target: target,
                        TargetPrincipal: targetPrincipal
                    );
                }
            case 8:
                return new WorldCommand.DissolveControl(
                    EntityIndex: entity,
                    Principal: principal,
                    TargetPrincipal: ReadPrincipal(reader: ref reader)
                );
            case 9:
                return ReadDurableState(
                    entity: entity,
                    principal: principal,
                    reader: ref reader
                );
            case 10:
                return new WorldCommand.RigidImpulse(
                    EntityIndex: entity,
                    Impulse: reader.ReadFiniteVector(field: "RigidImpulse.Impulse"),
                    Principal: principal
                );
            case 11:
                return new WorldCommand.CarryBody(
                    EntityIndex: entity,
                    Principal: principal,
                    TargetIndex: reader.ReadInt32()
                );
            case 12:
                return new WorldCommand.ReleaseCarry(
                    EntityIndex: entity,
                    Principal: principal
                );
            default:
                throw new LeafCodecException(failure: Fail(
                    detail: $"command discriminant {discriminant} is not declared",
                    refusal: WorldCodecRefusal.LeafKindUnknown
                ));
        }
    }
    private static WorldCommand.LoadDurableState ReadDurableState(ref WireReader reader, Principal principal, int entity) {
        var tick = reader.ReadUInt64();
        var values = reader.ReadArray(
            field: "LoadDurableState.Values",
            maximum: MaxDurableStateValues,
            readItem: static (ref WireReader r) => {
                var name = r.ReadRequiredString(field: "LoadDurableState.Values.Name");
                var value = r.ReadFixed();
                var timerTicks = r.ReadUInt64();

                return new DurableStateValue(
                    Name: name,
                    TimerTicks: timerTicks,
                    Value: value
                );
            }
        );

        if (
            !reader.Failed &&
            (values.Length == 0)
        ) {
            throw new LeafCodecException(failure: Fail(
                detail: $"durable state value count 0 is outside 1..{MaxDurableStateValues}",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        return new WorldCommand.LoadDurableState(
            EntityIndex: entity,
            Principal: principal,
            Tick: tick,
            Values: values
        );
    }
    private static WorldGrant ReadGrant(ref WireReader reader) {
        var grantee = ReadGrantee(reader: ref reader);
        var capability = WorldWireCodec.ReadCapability(reader: ref reader);
        var subject = WorldWireCodec.ReadSubject(reader: ref reader);
        var exclusive = reader.ReadBoolean();
        var budget = reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadUInt16());
        var reach = reader.ReadOptional(readValue: static (ref WireReader r) => new ChannelReachMask(Bits: r.ReadUInt64()));
        var consent = reader.ReadOptional(readValue: static (ref WireReader r) => new ChannelConsentMask(Bits: r.ReadUInt64()));
        var ceiling = reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadInt64());
        var kindMask = reader.ReadOptional(readValue: static (ref WireReader r) => new MutationKindMask(Bits: r.ReadUInt128()));
        var eventBudget = reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadUInt16());
        var holdCeiling = reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadInt64());
        var writeMask = reader.ReadOptional(readValue: static (ref WireReader r) => new DocumentWriteMask(Bits: r.ReadUInt64()));

        return new WorldGrant(
            Budget: budget,
            Capability: capability,
            Ceiling: ceiling,
            Consent: consent,
            EventBudget: eventBudget,
            Exclusive: exclusive,
            Grantee: grantee,
            HoldCeiling: holdCeiling,
            KindMask: kindMask,
            Reach: reach,
            Subject: subject,
            WriteMask: writeMask
        );
    }
    // The lever leaf is keyed by the knob's registered NAME, not an ordinal: the vocabulary is a composition-time
    // registration (Client.WorldSessionLevers), so the wire carries the token and the applier owns which tokens
    // resolve. An empty name can address no registration, so it is refused here rather than travelling.
    private static WorldSessionLever ReadLever(ref WireReader reader) {
        var section = WorldWireCodec.ReadSection(reader: ref reader);
        var name = reader.ReadString(field: "SessionLever.Name");
        var a = reader.ReadDouble();
        var b = reader.ReadDouble();
        var seat = reader.ReadInt32();

        if (
            !reader.Failed &&
            (name.Length == 0)
        ) {
            throw new LeafCodecException(failure: Fail(
                detail: "session lever name is empty",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        return new WorldSessionLever(
            A: a,
            B: b,
            Name: name,
            Seat: seat,
            Section: section
        );
    }
    private static WorldMachineOperation? ReadMachineOperation(ref WireReader reader) {
        var instance = reader.ReadRequiredString(field: "MachineOperation.Instance");
        var generation = reader.ReadUInt64();
        var operationId = reader.ReadRequiredString(field: "MachineOperation.OperationId");
        var payloadBytes = reader.ReadBlock(
            field: "MachineOperation.Payload",
            maxBytes: WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Operation)
        );

        if (reader.Failed) {
            return null;
        }

        try {
            using var document = JsonDocument.Parse(utf8Json: payloadBytes);

            return new WorldMachineOperation(
                instance,
                generation,
                operationId,
                document.RootElement
            );
        } catch (JsonException exception) {
            throw new LeafCodecException(failure: Fail(
                detail: $"machine operation payload is not valid JSON: {exception.Message}",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }
    }
    // The acting principal's canonical shape is ruled on the read side exactly as the write side rules it, so a
    // shape the encoder would refuse never decodes.
    private static Principal ReadPrincipal(ref WireReader reader) {
        var principal = WorldWireCodec.ReadPrincipal(reader: ref reader);

        if (
            !reader.Failed &&
            !TryValidatePrincipal(
                failure: out var failure,
                principal: principal
            )
        ) {
            throw new LeafCodecException(failure: failure);
        }

        return principal;
    }
    private static Grantee ReadGrantee(ref WireReader reader) {
        var grantee = WorldWireCodec.ReadGrantee(reader: ref reader);

        if (
            !reader.Failed &&
            !TryValidateGrantee(
                grantee,
                out var failure
            )
        ) {
            throw new LeafCodecException(failure: failure);
        }

        return grantee;
    }
    private static WorldRebuildRequest? ReadRebuild(ref WireReader reader) {
        var kind = WorldWireCodec.ReadRebuildKind(reader: ref reader);
        var force = reader.ReadBoolean();
        var pathHint = reader.ReadNullableString(field: "Rebuild.PathHint");
        var contentHash = reader.ReadNullableString(field: "Rebuild.ContentHash");
        var definition = (reader.ReadBoolean()
            ? ReadRebuildDefinition(reader: ref reader)
            : null
        );

        if (reader.Failed) {
            return null;
        }

        var request = new WorldRebuildRequest(
            ContentHash: contentHash,
            Definition: definition,
            Force: force,
            Kind: kind,
            PathHint: pathHint
        );

        if (!ValidRebuildShape(request: request)) {
            throw new LeafCodecException(failure: Fail(
                detail: $"rebuild request kind '{kind}' does not carry the shape its kind requires (a document, a path hint, and a content hash iff Kind is Load or Reload, none of the three for Reset)",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        return request;
    }
    private static WorldDefinition? ReadRebuildDefinition(ref WireReader reader) {
        var json = reader.ReadBlock(
            field: "Rebuild.Definition",
            maxBytes: WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Rebuild)
        );

        if (reader.Failed) {
            return null;
        }

        WorldDefinition definition;

        try {
            definition = WorldDefinitionSerialization.Deserialize(utf8Json: json);
        } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException)) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.PayloadMalformed,
                exception.Message
            ));
        }

        foreach (var grant in definition.Grants) {
            if (!TryValidateGrantee(
                grant.Grantee,
                out var principalFailure
            )) {
                throw new LeafCodecException(failure: principalFailure);
            }
        }

        return definition;
    }
    private static WorldScreenOp ReadScreenOp(ref WireReader reader) {
        var discriminant = reader.ReadByte();

        switch (discriminant) {
            case 0: {
                    var index = reader.ReadInt32();
                    var contentPath = reader.ReadRequiredString(field: "Insert.ContentPath");
                    var engineId = reader.ReadNullableString(field: "Insert.EngineId");
                    var options = reader.ReadNullableString(field: "Insert.Options");

                    return new WorldScreenOp.Insert(
                        ContentPath: contentPath,
                        EngineId: engineId,
                        Index: index,
                        Options: options
                    );
                }
            case 1:
                return new WorldScreenOp.Eject(Index: reader.ReadInt32());
            case 2: {
                    var index = reader.ReadInt32();
                    var entry = reader.ReadInt32();

                    return new WorldScreenOp.Select(
                        Entry: entry,
                        Index: index
                    );
                }
            case 3: {
                    var index = reader.ReadInt32();
                    var options = reader.ReadNullableString(field: "SetOptions.Options");

                    return new WorldScreenOp.SetOptions(
                        Index: index,
                        Options: options
                    );
                }
            case 4: {
                    var name = reader.ReadRequiredString(field: "Link.Name");
                    var members = reader.ReadArray(
                        field: "Link.Members",
                        maximum: (WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.ScreenOp) / sizeof(int)),
                        readItem: static (ref WireReader r) => r.ReadInt32()
                    );

                    return new WorldScreenOp.Link(
                        Members: members,
                        Name: name
                    );
                }
            case 5:
                return new WorldScreenOp.Unlink(Name: reader.ReadRequiredString(field: "Unlink.Name"));
            default:
                throw new LeafCodecException(failure: Fail(
                    detail: $"screen op discriminant {discriminant} is not declared",
                    refusal: WorldCodecRefusal.LeafKindUnknown
                ));
        }
    }
    private static WorldCommand ReadSnapPose(ref WireReader reader, Principal principal, int entity) {
        var mode = WorldWireCodec.ReadSnapPoseMode(reader: ref reader);
        var position = reader.ReadFiniteVector(field: "SnapPose.Position");
        var yaw = reader.ReadSingle();
        var pitch = reader.ReadSingle();
        var roll = reader.ReadSingle();

        return new WorldCommand.SnapPose(
            EntityIndex: entity,
            Mode: mode,
            PitchRadians: pitch,
            Position: position,
            Principal: principal,
            RollRadians: roll,
            YawRadians: yaw
        );
    }
    private static byte SessionKind(SessionRequest request) => request switch {
        SessionRequest.Join => 0,
        SessionRequest.Leave => 1,
        SessionRequest.SetIdentity => 2,
        SessionRequest.SetPopulation => 3,
        SessionRequest.SetPeerSource => 4,
        SessionRequest.RememberPreferredController => 5,
        _ => throw UnknownLeaf(value: request),
    };
    private static Type? SessionType(byte kind) => kind switch {
        0 => typeof(SessionRequest.Join),
        1 => typeof(SessionRequest.Leave),
        2 => typeof(SessionRequest.SetIdentity),
        3 => typeof(SessionRequest.SetPopulation),
        4 => typeof(SessionRequest.SetPeerSource),
        5 => typeof(SessionRequest.RememberPreferredController),
        _ => null,
    };
    private static bool TryDecodeJsonUnion<T>(ReadOnlySpan<byte> bytes, Func<byte, Type?> typeOf, out T? value, out WorldCodecFailure failure) where T : class {
        value = null;
        if (bytes.IsEmpty) {
            failure = Fail(
                WorldCodecRefusal.PayloadTruncated,
                $"{typeof(T).Name} payload has no discriminant"
            );
            return false;
        }
        var type = typeOf(bytes[0]);

        if (type is null) {
            failure = Fail(
                WorldCodecRefusal.LeafKindUnknown,
                $"{typeof(T).Name} discriminant {bytes[0]} is not declared"
            );
            return false;
        }
        try {
            value = (JsonSerializer.Deserialize(
                utf8Json: bytes[1..],
                returnType: type,
                options: Json
            ) as T);
            if (value is null) {
                failure = Fail(
                    WorldCodecRefusal.PayloadMalformed,
                    $"{type.Name} decoded as null"
                );
                return false;
            }
            failure = default;
            return true;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or JsonException or NotSupportedException)) {
            failure = Fail(
                WorldCodecRefusal.PayloadMalformed,
                exception.Message
            );
            return false;
        }
    }
    private static bool TryDecodeMutationCore(ReadOnlySpan<byte> bytes, out WorldMutation? mutation, out WorldCodecFailure failure, bool committed) {
        if (
            !TryDecodeJsonUnion(
            bytes: bytes,
            failure: out failure,
            typeOf: MutationType,
            value: out mutation
        ) ||
            (mutation is null)
        ) {
            return false;
        }
        if (!TryValidateMutationPrincipals(
            committed: committed,
            failure: out failure,
            mutation: mutation
        )) {
            mutation = null;
            return false;
        }
        return true;
    }
    private static bool TryEncodeJsonUnion<T>(T value, Func<T, byte> kind, out byte[] bytes, out WorldCodecFailure failure) where T : class {
        if (value is null) {
            bytes = [];
            failure = Fail(
                WorldCodecRefusal.PayloadMissing,
                $"{typeof(T).Name} is null"
            );
            return false;
        }
        try {
            var json = JsonSerializer.SerializeToUtf8Bytes(
                value: value,
                inputType: value.GetType(),
                options: Json
            );

            bytes = new byte[checked((json.Length + 1))];
            bytes[0] = kind(value);
            json.CopyTo(
                array: bytes,
                index: 1
            );
            failure = default;
            return true;
        } catch (LeafCodecException exception) {
            bytes = [];
            failure = exception.Failure;
            return false;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or JsonException or NotSupportedException or OverflowException)) {
            bytes = [];
            failure = Fail(
                WorldCodecRefusal.PayloadMalformed,
                exception.Message
            );
            return false;
        }
    }
    private static bool TryEncodeMutationCore(WorldMutation mutation, out byte[] bytes, out WorldCodecFailure failure, bool committed) {
        if (mutation is null) {
            bytes = [];
            failure = Fail(
                detail: "WorldMutation is null",
                refusal: WorldCodecRefusal.PayloadMissing
            );

            return false;
        }
        if (!TryValidateMutationPrincipals(
            committed: committed,
            failure: out failure,
            mutation: mutation
        )) {
            bytes = [];
            return false;
        }

        try {
            var entry = WorldMutationKindCatalog.All().FirstOrDefault(predicate: candidate => (candidate.Type == mutation.GetType()));

            if (entry.Type is null) {
                bytes = [];
                failure = Fail(
                    WorldCodecRefusal.LeafKindUnknown,
                    $"mutation kind '{mutation.GetType().Name}' is not cataloged"
                );
                return false;
            }
            return TryEncodeJsonUnion(
                mutation,
                _ => checked((byte)entry.Ordinal),
                out bytes,
                out failure
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or OverflowException)) {
            bytes = [];
            failure = Fail(
                WorldCodecRefusal.LeafKindUnknown,
                exception.Message
            );
            return false;
        }
    }
    // Reads one binary leaf to its end. A reader refusal outranks a codec refusal raised after it: once the reader has
    // latched, every later read yields zeros, so a shape check that then fails is reporting the zeros, not the bytes.
    private static bool TryRead<T>(ReadOnlySpan<byte> bytes, WireReadItem<T> read, out T? value, out WorldCodecFailure failure) {
        var reader = new WireReader(bytes: bytes);

        try {
            var decoded = read(reader: ref reader);

            if (reader.TryFinish(failure: out var wireFailure)) {
                value = decoded;
                failure = default;

                return true;
            }

            failure = WorldFrameCodec.ToCodecFailure(failure: wireFailure);
        } catch (LeafCodecException exception) {
            failure = (reader.Failed
                ? WorldFrameCodec.ToCodecFailure(failure: reader.Failure)
                : exception.Failure
            );
        }

        value = default;

        return false;
    }
    // The acting principal of a mutation is a live-runtime identity. The nested row of an UpsertGrant/RemoveGrant is
    // document data this mutation writes into the Grants section, and a document grantee is exactly the shape the
    // cross-document write-back channel reads back out of it — so that row admits one.
    private static bool TryValidateMutationPrincipals(WorldMutation mutation, out WorldCodecFailure failure, bool committed = false) {
        failure = default;
        if (mutation is WorldMutation.Batch batch) {
            if (!batch.TryValidateShape(reason: out var reason)) {
                failure = new WorldCodecFailure(
                    Detail: reason,
                    Refusal: WorldCodecRefusal.PayloadMalformed
                );
                return false;
            }
            for (var index = 0; (index < batch.Mutations.Count); index++) {
                if (!TryValidateMutationPrincipals(
                    batch.Mutations[index],
                    out failure,
                    committed
                )) { return false; }
            }
        }
        // Only the committed-journal codec admits the canonical structural actor. A nested grant row still passes
        // the normal validation below; no live submission decoder calls this with committed=true.
        if (
            !(committed && (mutation.Principal == Principal.World)) &&
            !TryValidatePrincipal(
            mutation.Principal,
            out failure
        )
        ) {
            return false;
        }
        var nested = mutation switch {
            WorldMutation.UpsertGrant value => value.Row.Grantee,
            WorldMutation.RemoveGrant value => value.Target.Grantee,
            _ => ((Grantee?)null),
        };

        return (
            (nested is not { } grantee) ||
            TryValidateGrantee(
            grantee,
            out failure,
            documentAllowed: true
        )
        );
    }
    private static bool TryValidatePrincipal(Principal principal, out WorldCodecFailure failure) {
        var valid = principal.Kind switch {
            PrincipalKind.Seat => ((principal.Index >= 0) && (principal.Name is null) && (principal.Generation == 0)),
            PrincipalKind.Console => ((principal.Index == 0) && (principal.Name is null) && (principal.Generation == 0)),
            PrincipalKind.Addon => ((principal.Index == 0) && !string.IsNullOrEmpty(value: principal.Name) && (principal.Generation == 0)),
            PrincipalKind.Peer => (WorldBodiesLimits.IsBodyIndex(index: principal.Index) && (principal.Name is null) && (principal.Generation > 0)),
            _ => false,
        };

        failure = (valid
            ? default
            : Fail(
                WorldCodecRefusal.PrincipalShapeInvalid,
                principal.Kind switch {
                    PrincipalKind.Peer when !WorldBodiesLimits.IsBodyIndex(index: principal.Index) => $"Peer principal index {principal.Index} is outside 0..{(WorldBodiesLimits.CapacityCeiling - 1)}",
                    PrincipalKind.World => $"{principal.Describe()} cannot act from outside — the world's own authored program acts inside this process (a rule's effects, a kit's generate effect) and is stamped by the server itself; a submitter claiming it would be asserting the world's structural exemption from outside",
                    _ => $"{principal.Kind} principal has index {principal.Index}, generation {principal.Generation}, name '{principal.Name}'",
                }
            )
        );
        return valid;
    }
    private static bool TryValidateGrantee(Grantee grantee, out WorldCodecFailure failure, bool documentAllowed = false) {
        switch (grantee.Kind) {
            case GranteeKind.Principal when (grantee.Principal.Kind == PrincipalKind.World):
                failure = Fail(
                    WorldCodecRefusal.PrincipalShapeInvalid,
                    $"{grantee.Describe()} holds no grant row — the world's authority is structural (the admission door admits it before consulting the table at all), so a row naming it would be accepted and inert; there is nothing to author. To change what the world's own program does, change the program: authoring a rule takes mutate section:rules, authoring a kit takes mutate section:kits"
                );

                return false;
            case GranteeKind.Principal when (grantee.Name is null):
                return TryValidatePrincipal(
                    grantee.Principal,
                    out failure
                );
            case GranteeKind.Group when ((grantee.Principal == default) && !string.IsNullOrEmpty(value: grantee.Name)):
            case GranteeKind.Document when (documentAllowed && (grantee.Principal == default) && !string.IsNullOrEmpty(value: grantee.Name)):
                failure = default;

                return true;
            case GranteeKind.Document when !documentAllowed:
                failure = Fail(
                    WorldCodecRefusal.PrincipalShapeInvalid,
                    $"{grantee.Describe()} has no live grant row — a document's capability is authored as a grant row with world.grant.set, which the cross-document write-back channel reads off the owner's document"
                );

                return false;
            default:
                failure = Fail(
                    WorldCodecRefusal.PrincipalShapeInvalid,
                    $"{grantee.Kind} grantee has principal '{grantee.Principal.Describe()}', name '{grantee.Name}'"
                );

                return false;
        }
    }
    private static bool TryWrite(Action<WireWriter> write, out byte[] bytes, out WorldCodecFailure failure) {
        try {
            var writer = new WireWriter();

            write(obj: writer);
            bytes = writer.ToArray();
            failure = default;
            return true;
        } catch (LeafCodecException exception) {
            bytes = [];
            failure = exception.Failure;
            return false;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or OverflowException)) {
            bytes = [];
            failure = Fail(
                WorldCodecRefusal.PayloadMalformed,
                exception.Message
            );
            return false;
        }
    }
    private static LeafCodecException UnknownEnum<T>(T value) where T : struct, Enum => new(failure: Fail(
        WorldCodecRefusal.EnumValueUnknown,
        $"{typeof(T).Name}.{value} has no wire value"
    ));
    private static LeafCodecException UnknownLeaf(object value) => new(failure: Fail(
        WorldCodecRefusal.LeafKindUnknown,
        $"leaf kind '{value.GetType().Name}' has no discriminant"
    ));
    private static bool ValidRebuildShape(WorldRebuildRequest request) => request.Kind switch {
        WorldRebuildKind.Reset => ((request.Definition is null) && (request.PathHint is null) && (request.ContentHash is null)),
        WorldRebuildKind.Load or WorldRebuildKind.Reload => ((request.Definition is not null) && (request.PathHint is not null) && (request.ContentHash is not null)),
        _ => false,
    };
    private static void WriteCapability(WireWriter writer, WorldCapability capability) {
        if (!WorldWireCodec.TryWriteCapability(
            capability: capability,
            writer: writer
        )) {
            throw UnknownEnum(value: capability);
        }
    }
    private static void WriteCommand(WireWriter writer, WorldCommand command) {
        if (command is null) {
            throw new LeafCodecException(failure: Fail(
                detail: "command is null",
                refusal: WorldCodecRefusal.PayloadMissing
            ));
        }
        WritePrincipal(
            writer,
            command.Principal
        );
        writer.WriteInt32(value: command.EntityIndex);
        switch (command) {
            case WorldCommand.SnapPose value:
                writer.WriteByte(value: 0);
                WriteSnapPose(
                    value: value,
                    writer: writer
                );
                break;
            case WorldCommand.EnqueueSegment value:
                writer.WriteByte(value: 1);
                WorldWireCodec.WriteIntent(
                    intent: value.Intent,
                    writer: writer
                );
                writer.WriteSingle(value: value.Seconds);
                break;
            case WorldCommand.PressChannel value:
                writer.WriteByte(value: 2);
                writer.WriteInt32(value: value.ChannelOrdinal);
                writer.WriteFixed(value: value.Value);
                writer.WriteOptional(
                    value: value.HoldSeconds,
                    writeValue: static (w, v) => w.WriteSingle(value: v)
                );
                break;
            case WorldCommand.SetBodyMotion value:
                writer.WriteByte(value: 3);
                writer.WriteString(value: value.BodyMotionProgram);
                break;
            case WorldCommand.SetControl value:
                writer.WriteByte(value: 4);
                WriteIntentSource(
                    writer,
                    value.Source
                );
                break;
            case WorldCommand.Reconcile value:
                writer.WriteByte(value: 5);
                writer.WriteSingle(value: value.X);
                writer.WriteSingle(value: value.Z);
                writer.WriteSingle(value: value.YawRadians);
                writer.WriteSingle(value: value.Seconds);
                break;
            case WorldCommand.Stop:
                writer.WriteByte(value: 6);
                break;
            case WorldCommand.ComposeControl value:
                writer.WriteByte(value: 7);
                WriteSubject(
                    writer,
                    value.Target
                );
                writer.WriteBoolean(value: value.Exclusive);
                WritePrincipal(
                    writer,
                    value.TargetPrincipal
                );
                break;
            case WorldCommand.DissolveControl value:
                writer.WriteByte(value: 8);
                WritePrincipal(
                    writer,
                    value.TargetPrincipal
                );
                break;
            case WorldCommand.LoadDurableState value:
                if (
                    (value.Values.Count < 1) ||
                    (value.Values.Count > MaxDurableStateValues)
                ) {
                    throw new LeafCodecException(failure: Fail(
                        detail: $"durable state value count {value.Values.Count} is outside 1..{MaxDurableStateValues}",
                        refusal: WorldCodecRefusal.PayloadMalformed
                    ));
                }
                writer.WriteByte(value: 9);
                writer.WriteUInt64(value: value.Tick);
                writer.WriteArray(
                    items: value.Values,
                    writeItem: static (w, state) => {
                        WriteRequiredString(
                            w,
                            state.Name,
                            "LoadDurableState.Values.Name"
                        );
                        w.WriteFixed(value: state.Value);
                        w.WriteUInt64(value: state.TimerTicks);
                    }
                );
                break;
            case WorldCommand.RigidImpulse value:
                writer.WriteByte(value: 10);
                WriteFiniteVector(
                    field: "RigidImpulse.Impulse",
                    value: value.Impulse,
                    writer: writer
                );
                break;
            case WorldCommand.CarryBody value:
                writer.WriteByte(value: 11);
                writer.WriteInt32(value: value.TargetIndex);
                break;
            case WorldCommand.ReleaseCarry:
                writer.WriteByte(value: 12);
                break;
            default:
                throw UnknownLeaf(value: command);
        }
    }
    // A presentation vector crosses only when every lane is finite, the same rule the decoder's ReadFiniteVector
    // applies, so an encoder never produces bytes its own decoder refuses.
    private static void WriteFiniteVector(WireWriter writer, Vector3 value, string field) {
        if (
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y) ||
            !float.IsFinite(f: value.Z)
        ) {
            throw new LeafCodecException(failure: Fail(
                detail: $"{field} is not finite",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        writer.WriteVector(value: value);
    }
    private static void WriteGrant(WireWriter writer, WorldGrant grant) {
        WriteGrantee(
            grantee: grant.Grantee,
            writer: writer
        );
        WriteCapability(
            capability: grant.Capability,
            writer: writer
        );
        WriteSubject(
            writer,
            grant.Subject
        );
        writer.WriteBoolean(value: grant.Exclusive);
        writer.WriteOptional(
            value: grant.Budget,
            writeValue: static (w, value) => w.WriteUInt16(value: value)
        );
        writer.WriteOptional(
            value: grant.Reach,
            writeValue: static (w, value) => w.WriteUInt64(value: value.Bits)
        );
        writer.WriteOptional(
            value: grant.Consent,
            writeValue: static (w, value) => w.WriteUInt64(value: value.Bits)
        );
        writer.WriteOptional(
            value: grant.Ceiling,
            writeValue: static (w, value) => w.WriteInt64(value: value)
        );
        writer.WriteOptional(
            value: grant.KindMask,
            writeValue: static (w, value) => w.WriteUInt128(value: value.Bits)
        );
        writer.WriteOptional(
            value: grant.EventBudget,
            writeValue: static (w, value) => w.WriteUInt16(value: value)
        );
        writer.WriteOptional(
            value: grant.HoldCeiling,
            writeValue: static (w, value) => w.WriteInt64(value: value)
        );
        writer.WriteOptional(
            value: grant.WriteMask,
            writeValue: static (w, value) => w.WriteUInt64(value: value.Bits)
        );
    }
    private static void WriteIntentSource(WireWriter writer, IntentSource value) {
        if (!WorldWireCodec.TryWriteIntentSource(
            source: value,
            writer: writer
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(IntentSource)} '{value}' is not declared"
            ));
        }
    }
    // The generic named-machine operation leaf: instance, expected generation, operation id, then the provider's JSON
    // payload as one length-prefixed block under the Operation kind's frame cap.
    private static void WriteMachineOperation(WireWriter writer, WorldMachineOperation operation) {
        if (operation is null) {
            throw new LeafCodecException(failure: Fail(
                detail: "machine operation is null",
                refusal: WorldCodecRefusal.PayloadMissing
            ));
        }
        WriteRequiredString(
            writer: writer,
            value: operation.Instance,
            field: "MachineOperation.Instance"
        );
        writer.WriteUInt64(value: operation.ExpectedGeneration);
        WriteRequiredString(
            writer: writer,
            value: operation.OperationId,
            field: "MachineOperation.OperationId"
        );
        byte[] payload;

        try {
            payload = Encoding.UTF8.GetBytes(s: operation.Payload.GetRawText());
        } catch (InvalidOperationException exception) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.PayloadMalformed,
                exception.Message
            ));
        }
        if (payload.Length > WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Operation)) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.PayloadTooLarge,
                $"machine operation payload length {payload.Length} exceeds {WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Operation)}"
            ));
        }
        writer.WriteBlock(value: payload);
    }
    private static void WritePrincipal(WireWriter writer, Principal principal) {
        if (!TryValidatePrincipal(
            failure: out var failure,
            principal: principal
        )) {
            throw new LeafCodecException(failure: failure);
        }
        if (!WorldWireCodec.TryWritePrincipal(
            principal: principal,
            writer: writer
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(PrincipalKind)}.{principal.Kind} has no live wire value"
            ));
        }
    }
    // A document grantee has no wire value here: this writer serves the live runtime grant leaves, and the
    // cross-document write-back channel reads a document's grants off the owner's document (Server.WorldOwnedWorlds),
    // never off the runtime table. Its row is authored with world.grant.set, a JSON-encoded document edit.
    private static void WriteGrantee(Grantee grantee, WireWriter writer) {
        if (!TryValidateGrantee(
            grantee,
            out var failure
        )) {
            throw new LeafCodecException(failure: failure);
        }
        if (!WorldWireCodec.TryWriteGrantee(
            grantee: grantee,
            writer: writer
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{grantee.Describe()} has no live wire value — its row is authored with world.grant.set (the document's Grants section, read by the cross-document write-back channel), never granted into the runtime table where nothing would read it"
            ));
        }
    }    // The rebuild leaf's own tagged union: one discriminant byte for WorldRebuildKind, the force flag, an optional
    // path hint, an optional content-hash pin, then — Load/Reload only — the embedded document through the document's
    // own canonical serializer (never a re-derived re-parse). Reset carries neither a path, a document, nor a
    // content-hash pin here: the base is server state, never client-supplied, and its CAS hash is computed at apply
    // time (WorldServer.ApplyRebuild), not known at submission — see ValidRebuildShape, checked on BOTH write and
    // read so a malformed request can never round-trip silently into a different shape than it claims.
    private static void WriteRebuild(WireWriter writer, WorldRebuildRequest request) {
        if (request is null) {
            throw new LeafCodecException(failure: Fail(
                detail: "rebuild request is null",
                refusal: WorldCodecRefusal.PayloadMissing
            ));
        }
        if (!ValidRebuildShape(request: request)) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.PayloadMalformed,
                $"rebuild request kind '{request.Kind}' does not carry the shape its kind requires (a document, a path hint, and a content hash iff Kind is Load or Reload, none of the three for Reset)"
            ));
        }
        WriteRebuildKind(
            kind: request.Kind,
            writer: writer
        );
        writer.WriteBoolean(value: request.Force);
        writer.WriteNullableString(value: request.PathHint);
        writer.WriteNullableString(value: request.ContentHash);
        writer.WriteOptionalClass(
            value: request.Definition,
            writeValue: static (w, definition) => {
                if (definition.Grants is { } grants) {
                    foreach (var grant in grants) {
                        if (!TryValidateGrantee(
                            grant.Grantee,
                            out var principalFailure
                        )) {
                            throw new LeafCodecException(failure: principalFailure);
                        }
                    }
                }
                byte[] json;

                try {
                    json = WorldDefinitionSerialization.Serialize(definition: definition);
                } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException)) {
                    throw new LeafCodecException(failure: Fail(
                        WorldCodecRefusal.PayloadMalformed,
                        exception.Message
                    ));
                }
                w.WriteBlock(value: json);
            }
        );
    }
    private static void WriteRebuildKind(WireWriter writer, WorldRebuildKind kind) {
        if (!WorldWireTags.TryToWire(
            value: kind,
            wire: out var wire
        )) {
            throw UnknownEnum(value: kind);
        }

        writer.WriteByte(value: wire);
    }
    private static void WriteRequiredString(WireWriter writer, string? value, string field) {
        if (string.IsNullOrWhiteSpace(value: value)) {
            throw new LeafCodecException(failure: Fail(
                detail: $"{field} is required and carries no text",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }
        writer.WriteString(value: value);
    }
    private static void WriteScreenOp(WireWriter writer, WorldScreenOp op) {
        if (op is null) {
            throw new LeafCodecException(failure: Fail(
                detail: "screen op is null",
                refusal: WorldCodecRefusal.PayloadMissing
            ));
        }
        switch (op) {
            case WorldScreenOp.Insert value:
                writer.WriteByte(value: 0);
                writer.WriteInt32(value: value.Index);
                WriteRequiredString(
                    writer,
                    value.ContentPath,
                    "Insert.ContentPath"
                );
                writer.WriteNullableString(value: value.EngineId);
                writer.WriteNullableString(value: value.Options);
                break;
            case WorldScreenOp.Eject value:
                writer.WriteByte(value: 1);
                writer.WriteInt32(value: value.Index);
                break;
            case WorldScreenOp.Select value:
                writer.WriteByte(value: 2);
                writer.WriteInt32(value: value.Index);
                writer.WriteInt32(value: value.Entry);
                break;
            case WorldScreenOp.SetOptions value:
                writer.WriteByte(value: 3);
                writer.WriteInt32(value: value.Index);
                writer.WriteNullableString(value: value.Options);
                break;
            case WorldScreenOp.Link value:
                writer.WriteByte(value: 4);
                WriteRequiredString(
                    writer,
                    value.Name,
                    "Link.Name"
                );
                writer.WriteArray(
                    items: value.Members,
                    writeItem: static (w, member) => w.WriteInt32(value: member)
                );
                break;
            case WorldScreenOp.Unlink value:
                writer.WriteByte(value: 5);
                WriteRequiredString(
                    writer,
                    value.Name,
                    "Unlink.Name"
                );
                break;
            default:
                throw UnknownLeaf(value: op);
        }
    }
    private static void WriteSection(WireWriter writer, WorldSection value) {
        if (!WorldWireTags.TryToWire(
            value: value,
            wire: out var wire
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(WorldSection)} value {((int)value)} is not declared"
            ));
        }
        writer.WriteByte(value: wire);
    }
    private static void WriteSnapPose(WireWriter writer, WorldCommand.SnapPose value) {
        if (!WorldWireTags.TryToWire(
            value: value.Mode,
            wire: out var mode
        )) {
            throw UnknownEnum(value: value.Mode);
        }

        writer.WriteByte(value: mode);
        WriteFiniteVector(
            field: "SnapPose.Position",
            value: value.Position,
            writer: writer
        );
        writer.WriteSingle(value: value.YawRadians);
        writer.WriteSingle(value: value.PitchRadians);
        writer.WriteSingle(value: value.RollRadians);
    }
    private static void WriteSubject(WireWriter writer, GrantSubject subject) {
        if (!WorldWireCodec.TryWriteSubject(
            subject: subject,
            writer: writer
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(GrantSubject)} {subject.Kind}:{subject.Value} has no wire value"
            ));
        }
    }

    /// <summary>Decodes one canonical leaf selected by its declared submission kind.</summary>
    public static bool TryDecode(WorldSubmissionKind kind, ReadOnlySpan<byte> bytes, out WorldSubmissionPayload? payload, out WorldCodecFailure failure) {
        payload = null;

        switch (kind) {
            case WorldSubmissionKind.Command:
                if (TryDecodeCommand(
                    bytes: bytes,
                    command: out var command,
                    failure: out failure
                )) {
                    payload = new WorldSubmissionPayload.Command(Value: command!);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Grant:
                if (TryDecodeGrant(
                    bytes: bytes,
                    failure: out failure,
                    grant: out var grant
                )) {
                    payload = new WorldSubmissionPayload.Grant(Value: grant);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Revoke:
                if (TryDecodeRevoke(
                    bytes: bytes,
                    failure: out failure,
                    revoke: out var revoke
                )) {
                    payload = new WorldSubmissionPayload.Revoke(Value: revoke);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Session:
                if (TryDecodeSession(
                    bytes: bytes,
                    failure: out failure,
                    request: out var session
                )) {
                    payload = new WorldSubmissionPayload.Session(Value: session!);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Rebuild:
                if (TryDecodeRebuild(
                    bytes: bytes,
                    failure: out failure,
                    request: out var rebuild
                )) {
                    payload = new WorldSubmissionPayload.Rebuild(Value: rebuild!);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Mutation:
                if (TryDecodeMutation(
                    bytes: bytes,
                    failure: out failure,
                    mutation: out var mutation
                )) {
                    payload = new WorldSubmissionPayload.Mutation(Value: mutation!);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Undo:
                if (TryDecodeUndo(
                    bytes: bytes,
                    count: out var count,
                    failure: out failure
                )) {
                    payload = new WorldSubmissionPayload.Undo(Count: count);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Composition:
                if (TryDecodeComposition(
                    bytes: bytes,
                    composition: out var composition,
                    failure: out failure
                )) {
                    payload = new WorldSubmissionPayload.Composition(Value: composition!);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Lever:
                if (TryDecodeLever(
                    bytes: bytes,
                    failure: out failure,
                    lever: out var lever
                )) {
                    payload = new WorldSubmissionPayload.Lever(Value: lever);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Query:
                if (TryDecodeQuery(
                    bytes: bytes,
                    failure: out failure,
                    query: out var query
                )) {
                    payload = new WorldSubmissionPayload.Query(Value: query!);
                    return true;
                }
                return false;
            case WorldSubmissionKind.ScreenOp:
                if (TryDecodeScreenOp(
                    bytes: bytes,
                    failure: out failure,
                    screenOp: out var screenOp
                )) {
                    payload = new WorldSubmissionPayload.ScreenOp(Value: screenOp!);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Designation:
                if (TryDecodeDesignation(
                    bytes: bytes,
                    designation: out var designation,
                    failure: out failure
                )) {
                    payload = new WorldSubmissionPayload.Designation(Value: designation);
                    return true;
                }
                return false;
            case WorldSubmissionKind.Operation:
                if (TryDecodeMachineOperation(
                    bytes: bytes,
                    failure: out failure,
                    operation: out var operation
                )) {
                    payload = new WorldSubmissionPayload.Operation(Value: operation!);
                    return true;
                }
                payload = null;
                return false;
            default:
                failure = Fail(
                    detail: $"submission wire kind {((byte)kind)} is not declared",
                    refusal: WorldCodecRefusal.FrameKindUnknown
                );
                return false;
        }
    }
    /// <summary>Decodes the command leaf.</summary>
    public static bool TryDecodeCommand(ReadOnlySpan<byte> bytes, out WorldCommand? command, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: ReadCommand,
            value: out command
        );
    /// <summary>Decodes a mutation already committed by a trusted authority, including its world-authored actor.
    /// This is a persistence leaf, never an external-submission decoder.</summary>
    /// <param name="bytes">The bounded catalog-tagged mutation bytes from trusted authority storage.</param>
    /// <param name="mutation">The decoded mutation, or null on refusal.</param>
    /// <param name="failure">The shape refusal, or default on success.</param>
    /// <returns>Whether the mutation and all principal shapes are admissible for a committed journal.</returns>
    public static bool TryDecodeCommittedMutation(ReadOnlySpan<byte> bytes, out WorldMutation? mutation, out WorldCodecFailure failure) =>
        TryDecodeMutationCore(
            bytes,
            out mutation,
            out failure,
            committed: true
        );
    /// <summary>Decodes the composition leaf.</summary>
    public static bool TryDecodeComposition(ReadOnlySpan<byte> bytes, out WorldComposition? composition, out WorldCodecFailure failure) =>
        TryDecodeJsonUnion(
            bytes: bytes,
            failure: out failure,
            typeOf: CompositionType,
            value: out composition
        );
    /// <summary>Decodes the designation leaf.</summary>
    public static bool TryDecodeDesignation(ReadOnlySpan<byte> bytes, out WorldDesignation designation, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: static (ref WireReader reader) => {
                var entityIndex = reader.ReadInt32();
                var register = reader.ReadString(field: "Designation.Register");
                var subject = WorldWireCodec.ReadSubject(reader: ref reader);
                var point = reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadFixedVector());

                return new WorldDesignation(
                    EntityIndex: entityIndex,
                    Point: point,
                    Register: register,
                    Subject: subject
                );
            },
            value: out designation
        );
    /// <summary>Decodes the grant leaf.</summary>
    public static bool TryDecodeGrant(ReadOnlySpan<byte> bytes, out WorldGrant grant, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: ReadGrant,
            value: out grant
        );
    /// <summary>Decodes the lever leaf.</summary>
    public static bool TryDecodeLever(ReadOnlySpan<byte> bytes, out WorldSessionLever lever, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: ReadLever,
            value: out lever
        );
    /// <summary>Decodes the generic named-machine operation leaf.</summary>
    public static bool TryDecodeMachineOperation(ReadOnlySpan<byte> bytes, out WorldMachineOperation? operation, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: ReadMachineOperation,
            value: out operation
        );
    /// <summary>Decodes the mutation leaf under its stable catalog ordinal.</summary>
    public static bool TryDecodeMutation(ReadOnlySpan<byte> bytes, out WorldMutation? mutation, out WorldCodecFailure failure) =>
        TryDecodeMutationCore(
            bytes,
            out mutation,
            out failure,
            committed: false
        );
    /// <summary>Decodes the query leaf.</summary>
    public static bool TryDecodeQuery(ReadOnlySpan<byte> bytes, out WorldQuery? query, out WorldCodecFailure failure) =>
        TryDecodeJsonUnion(
            bytes: bytes,
            failure: out failure,
            typeOf: QueryType,
            value: out query
        );
    /// <summary>Decodes the rebuild leaf.</summary>
    public static bool TryDecodeRebuild(ReadOnlySpan<byte> bytes, out WorldRebuildRequest? request, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: ReadRebuild,
            value: out request
        );
    /// <summary>Decodes the revoke leaf.</summary>
    public static bool TryDecodeRevoke(ReadOnlySpan<byte> bytes, out WorldGrant revoke, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: ReadGrant,
            value: out revoke
        );
    /// <summary>Decodes the screen-op leaf.</summary>
    public static bool TryDecodeScreenOp(ReadOnlySpan<byte> bytes, out WorldScreenOp? screenOp, out WorldCodecFailure failure) =>
        TryRead(
            bytes: bytes,
            failure: out failure,
            read: ReadScreenOp,
            value: out screenOp
        );
    /// <summary>Decodes the session leaf.</summary>
    public static bool TryDecodeSession(ReadOnlySpan<byte> bytes, out SessionRequest? request, out WorldCodecFailure failure) {
        if (
            !TryDecodeJsonUnion(
            bytes: bytes,
            failure: out failure,
            typeOf: SessionType,
            value: out request
        ) ||
            (request is null)
        ) {
            return false;
        }
        if (!TryValidatePrincipal(
            request.Principal,
            out failure
        )) {
            request = null;
            return false;
        }
        return true;
    }
    /// <summary>Decodes the undo leaf.</summary>
    public static bool TryDecodeUndo(ReadOnlySpan<byte> bytes, out int count, out WorldCodecFailure failure) {
        if (bytes.Length != sizeof(int)) {
            count = 0;
            failure = Fail(
                ((bytes.Length < sizeof(int))
                ? WorldCodecRefusal.PayloadTruncated
                : WorldCodecRefusal.PayloadTrailingBytes),
                $"undo payload is {bytes.Length} bytes; exactly 4 are required"
            );
            return false;
        }
        count = BinaryPrimitives.ReadInt32LittleEndian(source: bytes);
        failure = default;
        return true;
    }
    /// <summary>Encodes any closed submission payload through its canonical leaf.</summary>
    public static bool TryEncode(WorldSubmissionPayload payload, out WorldSubmissionKind kind, out byte[] bytes, out WorldCodecFailure failure) {
        if (payload is null) {
            kind = default;
            bytes = [];
            failure = Fail(
                detail: "the submission payload is null",
                refusal: WorldCodecRefusal.PayloadMissing
            );

            return false;
        }

        switch (payload) {
            case WorldSubmissionPayload.Command command:
                kind = WorldSubmissionKind.Command;
                return TryEncodeCommand(
                    command.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Grant grant:
                kind = WorldSubmissionKind.Grant;
                return TryEncodeGrant(
                    grant.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Revoke revoke:
                kind = WorldSubmissionKind.Revoke;
                return TryEncodeRevoke(
                    revoke.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Session session:
                kind = WorldSubmissionKind.Session;
                return TryEncodeSession(
                    session.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Rebuild rebuild:
                kind = WorldSubmissionKind.Rebuild;
                return TryEncodeRebuild(
                    rebuild.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Mutation mutation:
                kind = WorldSubmissionKind.Mutation;
                return TryEncodeMutation(
                    mutation.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Undo undo:
                kind = WorldSubmissionKind.Undo;
                return TryEncodeUndo(
                    undo.Count,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Composition composition:
                kind = WorldSubmissionKind.Composition;
                return TryEncodeComposition(
                    composition.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Lever lever:
                kind = WorldSubmissionKind.Lever;
                return TryEncodeLever(
                    lever.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Query query:
                kind = WorldSubmissionKind.Query;
                return TryEncodeQuery(
                    query.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.ScreenOp screenOp:
                kind = WorldSubmissionKind.ScreenOp;
                return TryEncodeScreenOp(
                    screenOp.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Designation designation:
                kind = WorldSubmissionKind.Designation;
                return TryEncodeDesignation(
                    designation.Value,
                    out bytes,
                    out failure
                );
            case WorldSubmissionPayload.Operation operation:
                kind = WorldSubmissionKind.Operation;
                return TryEncodeMachineOperation(
                    operation.Value,
                    out bytes,
                    out failure
                );
            default:
                kind = default;
                bytes = [];
                failure = Fail(
                    WorldCodecRefusal.PayloadKindUnknown,
                    $"submission payload kind '{payload.GetType().Name}' has no wire discriminant"
                );

                return false;
        }
    }
    /// <summary>Encodes the command leaf.</summary>
    public static bool TryEncodeCommand(WorldCommand command, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => WriteCommand(
                command: command,
                writer: writer
            ),
            out bytes,
            out failure
        );
    /// <summary>Encodes a mutation already committed by a trusted authority. Unlike live ingress, this permits
    /// the canonical world-authored actor; it does not grant authority or admit a new submission.</summary>
    /// <param name="mutation">The committed journal entry.</param>
    /// <param name="bytes">Its catalog-tagged bytes, or an empty array on refusal.</param>
    /// <param name="failure">The shape refusal, or default on success.</param>
    /// <returns>Whether the mutation is encodable with valid committed-journal principal shapes.</returns>
    public static bool TryEncodeCommittedMutation(WorldMutation mutation, out byte[] bytes, out WorldCodecFailure failure) =>
        TryEncodeMutationCore(
            mutation,
            out bytes,
            out failure,
            committed: true
        );
    /// <summary>Encodes the composition leaf.</summary>
    public static bool TryEncodeComposition(WorldComposition composition, out byte[] bytes, out WorldCodecFailure failure) =>
        TryEncodeJsonUnion(
            bytes: out bytes,
            failure: out failure,
            kind: CompositionKind,
            value: composition
        );
    /// <summary>Encodes the designation leaf.</summary>
    public static bool TryEncodeDesignation(WorldDesignation designation, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => {
                writer.WriteInt32(value: designation.EntityIndex);
                writer.WriteString(value: designation.Register);
                WriteSubject(
                    writer: writer,
                    subject: designation.Subject
                );
                writer.WriteOptional(
                    value: designation.Point,
                    writeValue: static (w, point) => w.WriteFixedVector(value: point)
                );
            },
            out bytes,
            out failure
        );
    /// <summary>Encodes the grant leaf.</summary>
    public static bool TryEncodeGrant(WorldGrant grant, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => WriteGrant(
                grant: grant,
                writer: writer
            ),
            out bytes,
            out failure
        );
    /// <summary>Encodes the lever leaf.</summary>
    public static bool TryEncodeLever(WorldSessionLever lever, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => {
                WriteSection(
                    writer,
                    lever.Section
                );
                writer.WriteString(value: lever.Name);
                writer.WriteDouble(value: lever.A);
                writer.WriteDouble(value: lever.B);
                writer.WriteInt32(value: lever.Seat);
            },
            out bytes,
            out failure
        );
    /// <summary>Encodes the generic named-machine operation leaf.</summary>
    public static bool TryEncodeMachineOperation(WorldMachineOperation operation, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => WriteMachineOperation(
                operation: operation,
                writer: writer
            ),
            out bytes,
            out failure
        );
    /// <summary>Encodes the mutation leaf under its stable catalog ordinal.</summary>
    public static bool TryEncodeMutation(WorldMutation mutation, out byte[] bytes, out WorldCodecFailure failure) =>
        TryEncodeMutationCore(
            mutation,
            out bytes,
            out failure,
            committed: false
        );
    /// <summary>Encodes the query leaf.</summary>
    public static bool TryEncodeQuery(WorldQuery query, out byte[] bytes, out WorldCodecFailure failure) =>
        TryEncodeJsonUnion(
            bytes: out bytes,
            failure: out failure,
            kind: QueryKind,
            value: query
        );
    /// <summary>Encodes the rebuild leaf: one discriminant byte for <see cref="WorldRebuildKind"/>, the force flag, an
    /// optional path hint, an optional content-hash pin, and — for <see cref="WorldRebuildKind.Load"/>/
    /// <see cref="WorldRebuildKind.Reload"/> only — the embedded document through the document's own canonical
    /// serializer. A binary leaf, like the addon-lifecycle leaf, not a JSON union: the shape is small and fixed. The
    /// content-hash pin is this envelope's own copy of the CAS value the replay tape later checks a re-read against
    /// (<c>WorldReplaySnapshot</c>'s own leaf, never this one, records it on the TAPE) — carried here so it survives
    /// the loopback's encode-then-decode round trip intact before <c>WorldServer.ApplyRebuild</c> ever sees it.</summary>
    public static bool TryEncodeRebuild(WorldRebuildRequest request, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => WriteRebuild(
                request: request,
                writer: writer
            ),
            out bytes,
            out failure
        );
    /// <summary>Encodes the revoke leaf. Revoke deliberately has its own pair even though its value shape is a grant.</summary>
    public static bool TryEncodeRevoke(WorldGrant revoke, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => WriteGrant(
                grant: revoke,
                writer: writer
            ),
            out bytes,
            out failure
        );
    /// <summary>Encodes the screen-op leaf.</summary>
    public static bool TryEncodeScreenOp(WorldScreenOp screenOp, out byte[] bytes, out WorldCodecFailure failure) =>
        TryWrite(
            writer => WriteScreenOp(
                op: screenOp,
                writer: writer
            ),
            out bytes,
            out failure
        );
    /// <summary>Encodes the session leaf.</summary>
    public static bool TryEncodeSession(SessionRequest request, out byte[] bytes, out WorldCodecFailure failure) {
        if (request is null) {
            bytes = [];
            failure = Fail(
                detail: "SessionRequest is null",
                refusal: WorldCodecRefusal.PayloadMissing
            );
            return false;
        }
        if (!TryValidatePrincipal(
            request.Principal,
            out failure
        )) {
            bytes = [];
            return false;
        }
        return TryEncodeJsonUnion(
            bytes: out bytes,
            failure: out failure,
            kind: SessionKind,
            value: request
        );
    }
    /// <summary>Encodes the undo leaf.</summary>
    public static bool TryEncodeUndo(int count, out byte[] bytes, out WorldCodecFailure failure) {
        bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            destination: bytes,
            value: count
        );
        failure = default;
        return true;
    }

    private sealed class LeafCodecException(WorldCodecFailure failure) : Exception(failure.ToString()) {
        public WorldCodecFailure Failure { get; } = failure;
    }
}
