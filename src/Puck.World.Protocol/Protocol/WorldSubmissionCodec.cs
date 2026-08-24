using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>The declared wire discriminants for the twelve submission payload leaves. Ordinal 11 (the retired
/// addon-lifecycle leaf — mount/unmount now travel as document rows, <c>UpsertAddon</c>/<c>RemoveAddon</c>, through
/// the ordinary <see cref="WorldSubmissionKind.Mutation"/> leaf) is unassigned and never reused, matching
/// <c>WorldMutationKindCatalog</c>'s own retired-ordinal precedent.</summary>
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
    ScreenOp = 12,
    /// <summary>A subject-bearing target-register write.</summary>
    Designation = 13,
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
/// The one canonical encoder/decoder pair for each of the twelve <see cref="WorldSubmissionPayload"/> leaves. The
/// wire framer, loopback, and replay tape all call these methods; none owns a second command/grant vocabulary.
/// </summary>
public static class WorldSubmissionCodec {
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    // The pinned mapping itself lives in WorldWireTags (Puck.World.Schema, beside the enums) — this wrapper keeps this
    // codec's own exception-based refusal shape (WorldCodecFailure) and its distinct "retired" wording.
    private static WorldCapability CapabilityFromWire(byte value) {
        if (WorldWireTags.TryFromWire(
            value: out WorldCapability capability,
            wire: value
        )) {
            return capability;
        }
        throw new LeafCodecException(failure: Fail(
            WorldCodecRefusal.EnumValueUnknown,
            (WorldWireTags.IsRetiredCapabilityWire(wire: value)
                ? $"{nameof(WorldCapability)} wire value {value} is retired"
                : $"{nameof(WorldCapability)} wire value {value} is not declared"
            )
        ));
    }
    private static byte CapabilityToWire(WorldCapability value) {
        if (WorldWireTags.TryToWire(
            value: value,
            wire: out var wire
        )) {
            return wire;
        }
        throw new LeafCodecException(failure: Fail(
            WorldCodecRefusal.EnumValueUnknown,
            $"{nameof(WorldCapability)}.{value} has no wire value"
        ));
    }
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
    // The lever leaf is keyed by the knob's registered NAME, not an ordinal: the vocabulary is a composition-time
    // registration (Client.WorldSessionLevers), so the wire carries the token and the applier owns which tokens
    // resolve. An empty name can address no registration, so it is refused here rather than travelling.
    private static string ReadLeverName(BinaryReader reader) {
        var value = reader.ReadString();

        if (value.Length == 0) {
            throw new LeafCodecException(failure: Fail(
                detail: "session lever name is empty",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        return value;
    }
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
        WorldQuery.PlayerTargets => 7,
        WorldQuery.Rules => 8,
        WorldQuery.Properties => 9,
        WorldQuery.Interactions => 10,
        WorldQuery.Contacts => 11,
        WorldQuery.GrantAllows => 12,
        WorldQuery.GrantHandleMint => 13,
        WorldQuery.GrantHandleResolve => 14,
        WorldQuery.PopulationChannels => 15,
        WorldQuery.ProfileCatalog => 16,
        WorldQuery.FindProfile => 17,
        WorldQuery.PreferredControllerProfile => 18,
        WorldQuery.MusicState => 19,
        WorldQuery.JudgeState => 20,
        _ => throw UnknownLeaf(value: value),
    };
    private static Type? QueryType(byte kind) => kind switch {
        0 => typeof(WorldQuery.PlayerWhere),
        1 => typeof(WorldQuery.PlayerChannels),
        2 => typeof(WorldQuery.WorldPlayers),
        3 => typeof(WorldQuery.ScreenState),
        4 => typeof(WorldQuery.InputHolds),
        5 => typeof(WorldQuery.PlayerState),
        7 => typeof(WorldQuery.PlayerTargets),
        8 => typeof(WorldQuery.Rules),
        9 => typeof(WorldQuery.Properties),
        10 => typeof(WorldQuery.Interactions),
        11 => typeof(WorldQuery.Contacts),
        12 => typeof(WorldQuery.GrantAllows),
        13 => typeof(WorldQuery.GrantHandleMint),
        14 => typeof(WorldQuery.GrantHandleResolve),
        15 => typeof(WorldQuery.PopulationChannels),
        16 => typeof(WorldQuery.ProfileCatalog),
        17 => typeof(WorldQuery.FindProfile),
        18 => typeof(WorldQuery.PreferredControllerProfile),
        19 => typeof(WorldQuery.MusicState),
        20 => typeof(WorldQuery.JudgeState),
        _ => null,
    };
    private static WorldCommand ReadCommand(BinaryReader reader) {
        var principal = ReadPrincipal(reader: reader);
        var entity = reader.ReadInt32();

        return reader.ReadByte() switch {
            0 => ReadSnapPose(
            entity: entity,
            principal: principal,
            reader: reader
        ),
            1 => new WorldCommand.EnqueueSegment(
            Principal: principal,
            EntityIndex: entity,
            Intent: ReadIntent(reader: reader),
            Seconds: reader.ReadSingle()
        ),
            2 => new WorldCommand.PressChannel(
            Principal: principal,
            EntityIndex: entity,
            ChannelOrdinal: reader.ReadInt32(),
            Value: new FixedQ4816(Value: reader.ReadInt64()),
            HoldSeconds: ReadOptional(
                reader,
                static r => r.ReadSingle()
            )
        ),
            3 => new WorldCommand.SetBodyMotion(
            Principal: principal,
            EntityIndex: entity,
            BodyMotionProgram: reader.ReadString()
        ),
            4 => new WorldCommand.SetControl(
            Principal: principal,
            EntityIndex: entity,
            Source: ReadIntentSource(reader: reader)
        ),
            5 => new WorldCommand.Reconcile(
            Principal: principal,
            EntityIndex: entity,
            X: reader.ReadSingle(),
            Z: reader.ReadSingle(),
            YawRadians: reader.ReadSingle(),
            Seconds: reader.ReadSingle()
        ),
            6 => new WorldCommand.Stop(
            EntityIndex: entity,
            Principal: principal
        ),
            7 => new WorldCommand.ComposeControl(
            Principal: principal,
            EntityIndex: entity,
            Target: ReadSubject(reader: reader),
            Exclusive: reader.ReadBoolean(),
            TargetPrincipal: ReadPrincipal(reader: reader)
        ),
            8 => new WorldCommand.DissolveControl(
            Principal: principal,
            EntityIndex: entity,
            TargetPrincipal: ReadPrincipal(reader: reader)
        ),
            9 => ReadDurableState(
            entity: entity,
            principal: principal,
            reader: reader
        ),
            var wire => throw new LeafCodecException(failure: Fail(
            detail: $"command discriminant {wire} is not declared",
            refusal: WorldCodecRefusal.LeafKindUnknown
        )),
        };
    }
    private static WorldCommand.LoadDurableState ReadDurableState(BinaryReader reader, WorldPrincipal principal, int entity) {
        var tick = reader.ReadUInt64();
        var count = reader.ReadInt32();

        if (
            (count < 1) ||
            (count > 256)
        ) {
            throw new LeafCodecException(failure: Fail(
                detail: $"durable state value count {count} is outside 1..256",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        var values = new DurableStateValue[count];

        for (var index = 0; (index < count); index++) {
            values[index] = new DurableStateValue(
                Name: ReadRequiredString(
                    field: "LoadDurableState.Values.Name",
                    reader: reader
                ),
                Value: new FixedQ4816(Value: reader.ReadInt64()),
                TimerTicks: reader.ReadUInt64()
            );
        }

        return new WorldCommand.LoadDurableState(
            EntityIndex: entity,
            Principal: principal,
            Tick: tick,
            Values: values
        );
    }
    private static WorldGrant ReadGrant(BinaryReader reader) => new(
        Principal: ReadPrincipal(reader: reader),
        Capability: CapabilityFromWire(value: reader.ReadByte()),
        Subject: ReadSubject(reader: reader),
        Exclusive: reader.ReadBoolean(),
        Budget: ReadOptional(
            reader,
            static r => r.ReadUInt16()
        ),
        Reach: ReadOptional(
            reader,
            static r => new ChannelReachMask(Bits: r.ReadUInt64())
        ),
        Consent: ReadOptional(
            reader,
            static r => new ChannelConsentMask(Bits: r.ReadUInt64())
        ),
        Ceiling: ReadOptional(
            reader,
            static r => r.ReadInt64()
        ),
        KindMask: ReadOptional(
            reader,
            static r => new MutationKindMask(Bits: ReadKindMaskBits(reader: r))
        ),
        EventBudget: ReadOptional(
            reader,
            static r => r.ReadUInt16()
        ),
        HoldCeiling: ReadOptional(
            reader,
            static r => r.ReadInt64()
        ),
        WriteMask: ReadOptional(
            reader,
            static r => new DocumentWriteMask(Bits: r.ReadUInt64())
        )
    );
    private static PlayerIntent ReadIntent(BinaryReader reader) {
        var intent = default(PlayerIntent);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            intent = intent.WithChannel(
                ordinal: ordinal,
                value: new FixedQ4816(Value: reader.ReadInt64())
            );
        }
        return intent;
    }
    private static IntentSource ReadIntentSource(BinaryReader reader) => reader.ReadByte() switch {
        0 => IntentSource.Live,
        1 => IntentSource.Idle,
        2 => IntentSource.Producer(name: reader.ReadString()),
        var value => throw new LeafCodecException(failure: Fail(
        WorldCodecRefusal.EnumValueUnknown,
        $"{nameof(IntentSource)} wire value {value} is not declared"
    )),
    };
    private static UInt128 ReadKindMaskBits(BinaryReader reader) {
        var low = reader.ReadUInt64();

        return (((UInt128)reader.ReadUInt64()) << 64) | low;
    }
    private static string? ReadNullableString(BinaryReader reader) => (reader.ReadBoolean()
        ? reader.ReadString()
        : null
    );
    private static T? ReadOptional<T>(BinaryReader reader, Func<BinaryReader, T> read) where T : struct => (reader.ReadBoolean()
        ? read(reader)
        : null
    );
    private static WorldPrincipal ReadPrincipal(BinaryReader reader) {
        var wireKind = reader.ReadByte();

        if (!WorldWireTags.TryFromWire(
            value: out PrincipalKind kind,
            wire: wireKind
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(PrincipalKind)} wire value {wireKind} is not declared"
            ));
        }
        var principal = new WorldPrincipal(
            Kind: kind,
            Index: reader.ReadInt32(),
            Generation: reader.ReadInt32(),
            Name: ReadNullableString(reader: reader)
        );
        // Reuse the write-side shape ruling without exposing an exception to the caller.
        using var sink = new MemoryStream();
        using var writer = new BinaryWriter(output: sink);

        WritePrincipal(
            principal: principal,
            writer: writer
        );
        return principal;
    }
    private static WorldRebuildRequest ReadRebuild(BinaryReader reader) {
        var kind = RebuildKindFromWire(value: reader.ReadByte());
        var force = reader.ReadBoolean();
        var pathHint = ReadNullableString(reader: reader);
        var contentHash = ReadNullableString(reader: reader);
        var hasDefinition = reader.ReadBoolean();
        WorldDefinition? definition = null;

        if (hasDefinition) {
            var length = reader.ReadInt32();

            if (length < 0) {
                throw new LeafCodecException(failure: Fail(
                    detail: $"rebuild document length {length} is negative",
                    refusal: WorldCodecRefusal.PayloadMalformed
                ));
            }

            var json = reader.ReadBytes(count: length);

            if (json.Length != length) {
                throw new LeafCodecException(failure: Fail(
                    WorldCodecRefusal.PayloadTruncated,
                    $"rebuild document declares {length} bytes; {json.Length} remained"
                ));
            }

            try {
                definition = WorldDefinitionSerialization.Deserialize(utf8Json: json);
            } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException)) {
                throw new LeafCodecException(failure: Fail(
                    WorldCodecRefusal.PayloadMalformed,
                    exception.Message
                ));
            }

            foreach (var grant in definition.Grants) {
                if (!TryValidatePrincipal(
                    grant.Principal,
                    out var principalFailure
                )) {
                    throw new LeafCodecException(failure: principalFailure);
                }
            }
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
    private static string ReadRequiredString(BinaryReader reader, string field) {
        var value = reader.ReadString();

        if (string.IsNullOrEmpty(value: value)) {
            throw new LeafCodecException(failure: Fail(
                detail: $"{field} is null or empty",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        return value;
    }
    private static WorldScreenOp ReadScreenOp(BinaryReader reader) {
        return reader.ReadByte() switch {
            0 => new WorldScreenOp.Insert(
            Index: reader.ReadInt32(),
            ContentPath: ReadRequiredString(
                field: "Insert.ContentPath",
                reader: reader
            ),
            EngineId: ReadNullableString(reader: reader),
            Options: ReadNullableString(reader: reader)
        ),
            1 => new WorldScreenOp.Eject(Index: reader.ReadInt32()),
            2 => new WorldScreenOp.Select(
            Index: reader.ReadInt32(),
            Entry: reader.ReadInt32()
        ),
            3 => new WorldScreenOp.SetOptions(
            Index: reader.ReadInt32(),
            Options: ReadNullableString(reader: reader)
        ),
            4 => ReadScreenOpLink(reader: reader),
            5 => new WorldScreenOp.Unlink(Name: ReadRequiredString(
            field: "Unlink.Name",
            reader: reader
        )),
            var wire => throw new LeafCodecException(failure: Fail(
            detail: $"screen op discriminant {wire} is not declared",
            refusal: WorldCodecRefusal.LeafKindUnknown
        )),
        };
    }
    private static WorldScreenOp.Link ReadScreenOpLink(BinaryReader reader) {
        var name = ReadRequiredString(
            field: "Link.Name",
            reader: reader
        );
        var count = reader.ReadInt32();

        if (count < 0) {
            throw new LeafCodecException(failure: Fail(
                detail: $"screen op link member count {count} is negative",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }

        var members = new List<int>(capacity: count);

        for (var index = 0; (index < count); index++) {
            members.Add(item: reader.ReadInt32());
        }

        return new WorldScreenOp.Link(
            Members: members,
            Name: name
        );
    }
    private static WorldSection ReadSection(BinaryReader reader) {
        var wire = reader.ReadByte();

        if (!WorldWireTags.TryFromWire(
            value: out WorldSection value,
            wire: wire
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(WorldSection)} wire value {wire} is not declared"
            ));
        }
        return value;
    }
    private static WorldCommand.SnapPose ReadSnapPose(BinaryReader reader, WorldPrincipal principal, int entity) => SnapPoseModeFromWire(value: reader.ReadByte()) switch {
        SnapPoseMode.Pose => new WorldCommand.SnapPose(
        Principal: principal,
        EntityIndex: entity,
        Position: ReadVector(reader: reader),
        YawRadians: reader.ReadSingle(),
        PitchRadians: reader.ReadSingle(),
        RollRadians: reader.ReadSingle(),
        Mode: SnapPoseMode.Pose
    ),
        var mode => throw UnknownEnum(value: mode),
    };
    private static GrantSubject ReadSubject(BinaryReader reader) {
        var wireKind = reader.ReadByte();

        if (!WorldWireTags.TryFromWire(
            value: out GrantSubjectKind kind,
            wire: wireKind
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(GrantSubjectKind)} wire value {wireKind} is not declared"
            ));
        }
        var value = ((kind == GrantSubjectKind.Section)
            ? (int)ReadSection(reader: reader)
            : reader.ReadInt32()
        );

        return new GrantSubject(
            Kind: kind,
            Value: value,
            Id: ReadNullableString(reader: reader)
        );
    }
    private static Vector3 ReadVector(BinaryReader reader) => new(
        x: reader.ReadSingle(),
        y: reader.ReadSingle(),
        z: reader.ReadSingle()
    );
    private static WorldRebuildKind RebuildKindFromWire(byte value) => value switch {
        0 => WorldRebuildKind.Reset,
        1 => WorldRebuildKind.Load,
        2 => WorldRebuildKind.Reload,
        _ => throw new LeafCodecException(failure: Fail(
        WorldCodecRefusal.EnumValueUnknown,
        $"{nameof(WorldRebuildKind)} wire value {value} is not declared"
    )),
    };
    private static byte RebuildKindToWire(WorldRebuildKind value) => value switch {
        WorldRebuildKind.Reset => 0,
        WorldRebuildKind.Load => 1,
        WorldRebuildKind.Reload => 2,
        _ => throw new LeafCodecException(failure: Fail(
        WorldCodecRefusal.EnumValueUnknown,
        $"{nameof(WorldRebuildKind)}.{value} has no wire value"
    )),
    };
    // THE LEAF-DISCRIMINANT RETIREMENT CONVENTION, stated once for every union below (session, composition,
    // query, grant subject): a discriminant is retired by leaving its byte unassigned, never by handing that
    // byte to a different leaf. The successor takes the next free value, so a stray retired byte decodes to
    // unknown and fails loudly (LeafKindUnknown / EnumValueUnknown) rather than silently reading as some
    // other leaf. This holds even though the wire is in-session-only and has zero consumers: the
    // discriminant tables here are the record of what each byte has ever meant, and reuse erases it.
    // (`CapabilityFromWire`'s retired 2 spells its refusal out explicitly instead of falling through,
    // because that reader is a bare value map with no type table to return null from — the same convention,
    // said the only way that shape can say it.)
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
    private static SnapPoseMode SnapPoseModeFromWire(byte value) => value switch { 0 => SnapPoseMode.Pose, _ => throw UnknownWire<SnapPoseMode>(value: value) };
    private static byte SnapPoseModeToWire(SnapPoseMode value) => value switch { SnapPoseMode.Pose => 0, _ => throw UnknownEnum(value: value) };
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
    private static bool TryRead<T>(ReadOnlySpan<byte> bytes, Func<BinaryReader, T> read, out T? value, out WorldCodecFailure failure) where T : class {
        try {
            using var stream = new MemoryStream(
                bytes.ToArray(),
                writable: false
            );
            using var reader = new BinaryReader(
                input: stream,
                encoding: Encoding.UTF8,
                leaveOpen: true
            );

            value = read(reader);
            if (stream.Position != stream.Length) {
                failure = Fail(
                    WorldCodecRefusal.PayloadTrailingBytes,
                    $"{(stream.Length - stream.Position)} byte(s) follow the canonical leaf"
                );
                value = null;
                return false;
            }
            failure = default;
            return true;
        } catch (LeafCodecException exception) {
            value = null;
            failure = exception.Failure;
            return false;
        } catch (EndOfStreamException exception) {
            value = null;
            failure = Fail(
                WorldCodecRefusal.PayloadTruncated,
                exception.Message
            );
            return false;
        } catch (Exception exception) when ((exception is ArgumentException or FormatException or IOException or OverflowException)) {
            value = null;
            failure = Fail(
                WorldCodecRefusal.PayloadMalformed,
                exception.Message
            );
            return false;
        }
    }
    private static bool TryReadStruct<T>(ReadOnlySpan<byte> bytes, Func<BinaryReader, T> read, out T value, out WorldCodecFailure failure) where T : struct {
        try {
            using var stream = new MemoryStream(
                bytes.ToArray(),
                writable: false
            );
            using var reader = new BinaryReader(
                input: stream,
                encoding: Encoding.UTF8,
                leaveOpen: true
            );

            value = read(reader);
            if (stream.Position != stream.Length) {
                failure = Fail(
                    WorldCodecRefusal.PayloadTrailingBytes,
                    $"{(stream.Length - stream.Position)} byte(s) follow the canonical leaf"
                );
                value = default;
                return false;
            }
            failure = default;
            return true;
        } catch (LeafCodecException exception) {
            value = default;
            failure = exception.Failure;
            return false;
        } catch (EndOfStreamException exception) {
            value = default;
            failure = Fail(
                WorldCodecRefusal.PayloadTruncated,
                exception.Message
            );
            return false;
        } catch (Exception exception) when ((exception is ArgumentException or FormatException or IOException or OverflowException)) {
            value = default;
            failure = Fail(
                WorldCodecRefusal.PayloadMalformed,
                exception.Message
            );
            return false;
        }
    }
    // The ACTING principal of a mutation is a live-runtime identity and never a document (a document does not act; it
    // is acted upon). The NESTED row of an UpsertGrant/RemoveGrant is the opposite case: that row is DOCUMENT DATA
    // this mutation writes into the Grants section, and a document principal is exactly the shape the cross-document
    // write-back channel reads back out of it — so the row admits one where the actor never does. Same shape rule,
    // two different admissible kind sets, spelled by the caller rather than guessed at inside the rule. A market
    // mutation's trade party (Seller/Bidder/Buyer/Canceler) is a third case, shaped like the acting principal
    // (documentAllowed stays false — a document can no more trade than it can act) but never itself the acting
    // principal: Server.WorldServer's own TryAuthorizeMarketParty is what checks a party against the actor
    // submitting it, a live-runtime question this wire-shape check cannot answer and does not attempt to.
    private static bool TryValidateMutationPrincipals(WorldMutation mutation, out WorldCodecFailure failure) {
        if (!TryValidatePrincipal(
            mutation.Principal,
            out failure
        )) {
            return false;
        }
        var nested = mutation switch {
            WorldMutation.UpsertGrant value => value.Row.Principal,
            WorldMutation.RemoveGrant value => value.Target.Principal,
            _ => ((WorldPrincipal?)null),
        };

        if (
            (nested is { } principal) &&
            !TryValidatePrincipal(
            principal,
            out failure,
            documentAllowed: true
        )
        ) {
            return false;
        }
        var party = mutation switch {
            WorldMutation.CreateMarketListing value => value.Seller,
            WorldMutation.PlaceMarketBid value => value.Bidder,
            WorldMutation.BuyoutMarketListing value => value.Buyer,
            WorldMutation.CancelMarketListing value => value.Canceler,
            _ => ((WorldPrincipal?)null),
        };

        return (
            (party is not { } tradeParty) ||
            TryValidatePrincipal(
            tradeParty,
            out failure
        )
        );
    }
    private static bool TryValidatePrincipal(WorldPrincipal principal, out WorldCodecFailure failure, bool documentAllowed = false) {
        var valid = principal.Kind switch {
            PrincipalKind.Seat => ((principal.Index >= 0) && (principal.Name is null) && (principal.Generation == 0)),
            PrincipalKind.Console => ((principal.Index == 0) && (principal.Name is null) && (principal.Generation == 0)),
            PrincipalKind.Addon => ((principal.Index == 0) && !string.IsNullOrEmpty(value: principal.Name) && (principal.Generation == 0)),
            PrincipalKind.Peer => (WorldPopulationLimits.IsPeerIndex(index: principal.Index) && (principal.Name is null) && (principal.Generation > 0)),
            PrincipalKind.Document => (documentAllowed && (principal.Index == 0) && !string.IsNullOrEmpty(value: principal.Name) && (principal.Generation == 0)),
            // Group's shape mirrors Addon's (Index 0, a non-empty Name carrying the id, Generation 0) but is valid
            // UNCONDITIONALLY — never gated behind documentAllowed — because unlike Document a group IS a real live
            // grant target, not a cross-document-only concept.
            PrincipalKind.Group => ((principal.Index == 0) && !string.IsNullOrEmpty(value: principal.Name) && (principal.Generation == 0)),
            _ => false,
        };

        failure = (valid
            ? default
            : Fail(
                WorldCodecRefusal.PrincipalShapeInvalid,
                (principal.Kind, documentAllowed) switch {
                    (PrincipalKind.Peer, _) when !WorldPopulationLimits.IsPeerIndex(index: principal.Index) => $"Peer principal index {principal.Index} is outside {WorldPopulationLimits.LocalSeatCount}..{(WorldPopulationLimits.CapacityCeiling - 1)}",
                    (PrincipalKind.Document, false) => $"{principal.Describe()} cannot ACT — a document is written to, never a submitter; its capability is authored as a grant ROW with world.grant.set, which the cross-document write-back channel reads off the owner's document",
                    // The World principal is refused on BOTH sides of this rule, for two DIFFERENT reasons — one message
                    // each, because the shared one told a console-typed `world.grant.set world …` that it was an
                    // off-process submitter, which is not what it is or why it is refused.
                    (PrincipalKind.World, false) => $"{principal.Describe()} cannot ACT — the world's own authored program acts INSIDE this process (a rule's effects, a kit's generate effect) and is stamped by the server itself; a submitter claiming it would be asserting the world's structural exemption from outside",
                    (PrincipalKind.World, true) => $"{principal.Describe()} holds no grant ROW — the world's authority is STRUCTURAL (the admission door admits it before consulting the table at all), so a row naming it would be accepted and inert; there is nothing to author. To change what the world's own program does, change the program: authoring a rule takes mutate section:rules, authoring a kit takes mutate section:kits",
                    _ => $"{principal.Kind} principal has index {principal.Index}, generation {principal.Generation}, name '{principal.Name}'",
                }
            )
        );
        return valid;
    }
    private static bool TryWrite(Action<BinaryWriter> write, out byte[] bytes, out WorldCodecFailure failure) {
        try {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(
                output: stream,
                encoding: Encoding.UTF8,
                leaveOpen: true
            );

            write(writer);
            writer.Flush();
            bytes = stream.ToArray();
            failure = default;
            return true;
        } catch (LeafCodecException exception) {
            bytes = [];
            failure = exception.Failure;
            return false;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or IOException or OverflowException)) {
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
    private static LeafCodecException UnknownWire<T>(byte value) where T : struct, Enum => new(failure: Fail(
        WorldCodecRefusal.EnumValueUnknown,
        $"{typeof(T).Name} wire value {value} is not declared"
    ));
    private static bool ValidRebuildShape(WorldRebuildRequest request) => request.Kind switch {
        WorldRebuildKind.Reset => ((request.Definition is null) && (request.PathHint is null) && (request.ContentHash is null)),
        WorldRebuildKind.Load or WorldRebuildKind.Reload => ((request.Definition is not null) && (request.PathHint is not null) && (request.ContentHash is not null)),
        _ => false,
    };
    private static void WriteCommand(BinaryWriter writer, WorldCommand command) {
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
        writer.Write(value: command.EntityIndex);
        switch (command) {
            case WorldCommand.SnapPose value:
                writer.Write(value: ((byte)0)); WriteSnapPose(
                    value: value,
                    writer: writer
                ); break;
            case WorldCommand.EnqueueSegment value:
                writer.Write(value: ((byte)1)); WriteIntent(
                    writer,
                    value.Intent
                ); writer.Write(value: value.Seconds); break;
            case WorldCommand.PressChannel value:
                writer.Write(value: ((byte)2)); writer.Write(value: value.ChannelOrdinal); writer.Write(value: value.Value.Value); WriteOptional(
                    writer,
                    value.HoldSeconds,
                    static (w, v) => w.Write(value: v)
                ); break;
            case WorldCommand.SetBodyMotion value:
                writer.Write(value: ((byte)3)); writer.Write(value: value.BodyMotionProgram); break;
            case WorldCommand.SetControl value:
                writer.Write(value: ((byte)4)); WriteIntentSource(
                    writer,
                    value.Source
                ); break;
            case WorldCommand.Reconcile value:
                writer.Write(value: ((byte)5)); writer.Write(value: value.X); writer.Write(value: value.Z); writer.Write(value: value.YawRadians); writer.Write(value: value.Seconds); break;
            case WorldCommand.Stop:
                writer.Write(value: ((byte)6)); break;
            case WorldCommand.ComposeControl value:
                writer.Write(value: ((byte)7)); WriteSubject(
                    writer,
                    value.Target
                ); writer.Write(value: value.Exclusive); WritePrincipal(
                    writer,
                    value.TargetPrincipal
                ); break;
            case WorldCommand.DissolveControl value:
                writer.Write(value: ((byte)8)); WritePrincipal(
                    writer,
                    value.TargetPrincipal
                ); break;
            case WorldCommand.LoadDurableState value:
                writer.Write(value: ((byte)9));
                writer.Write(value: value.Tick);
                writer.Write(value: value.Values.Count);
                foreach (var state in value.Values) {
                    WriteRequiredString(
                        writer,
                        state.Name,
                        "LoadDurableState.Values.Name"
                    );
                    writer.Write(value: state.Value.Value);
                    writer.Write(value: state.TimerTicks);
                }
                break;
            default:
                throw UnknownLeaf(value: command);
        }
    }
    private static void WriteGrant(BinaryWriter writer, WorldGrant grant) {
        WritePrincipal(
            writer,
            grant.Principal
        );
        writer.Write(value: CapabilityToWire(value: grant.Capability));
        WriteSubject(
            writer,
            grant.Subject
        );
        writer.Write(value: grant.Exclusive);
        WriteOptional(
            writer,
            grant.Budget,
            static (w, value) => w.Write(value: value)
        );
        WriteOptional(
            writer,
            grant.Reach,
            static (w, value) => w.Write(value: value.Bits)
        );
        WriteOptional(
            writer,
            grant.Consent,
            static (w, value) => w.Write(value: value.Bits)
        );
        WriteOptional(
            writer,
            grant.Ceiling,
            static (w, value) => w.Write(value: value)
        );
        WriteOptional(
            writer,
            grant.KindMask,
            static (w, value) => WriteKindMaskBits(
                writer: w,
                bits: value.Bits
            )
        );
        WriteOptional(
            writer,
            grant.EventBudget,
            static (w, value) => w.Write(value: value)
        );
        WriteOptional(
            writer,
            grant.HoldCeiling,
            static (w, value) => w.Write(value: value)
        );
        WriteOptional(
            writer,
            grant.WriteMask,
            static (w, value) => w.Write(value: value.Bits)
        );
    }
    private static void WriteIntent(BinaryWriter writer, PlayerIntent intent) {
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            writer.Write(value: intent[ordinal].Value);
        }
    }
    private static void WriteIntentSource(BinaryWriter writer, IntentSource value) {
        if (value.IsLive) {
            writer.Write(value: ((byte)0));
        } else if (value.IsIdle) {
            writer.Write(value: ((byte)1));
        } else if (value.ProducerName is { } name) {
            writer.Write(value: ((byte)2));
            writer.Write(value: name);
        } else {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(IntentSource)} '{value}' is not declared"
            ));
        }
    }
    // The kind mask rides SIXTEEN bytes, low half first, because its lane is UInt128 — BinaryWriter has no UInt128
    // overload, and that absence is load-bearing here: the obvious `w.Write((ulong)value.Bits)` COMPILES, round-trips
    // ordinals 0-63 perfectly, and silently drops every bit above 63, so a truncated mask would read back as a
    // plausible grant that merely admits fewer kinds than authored. Spelling both halves explicitly is what makes
    // that failure impossible rather than merely unlikely. Bits 0-63 occupy the same first eight bytes, in the same
    // order, that this leaf has always written — the widen appends, it does not re-lay.
    private static void WriteKindMaskBits(BinaryWriter writer, UInt128 bits) {
        writer.Write(value: ((ulong)bits));
        writer.Write(value: ((ulong)(bits >> 64)));
    }
    private static void WriteNullableString(BinaryWriter writer, string? value) {
        writer.Write(value: (value is not null));
        if (value is not null) {
            writer.Write(value: value);
        }
    }
    private static void WriteOptional<T>(BinaryWriter writer, T? value, Action<BinaryWriter, T> write) where T : struct {
        writer.Write(value: value.HasValue);
        if (value is { } present) {
            write(
                writer,
                present
            );
        }
    }
    private static void WritePrincipal(BinaryWriter writer, WorldPrincipal principal) {
        if (!TryValidatePrincipal(
            principal,
            out var failure
        )) {
            throw new LeafCodecException(failure: failure);
        }
        // PrincipalKind.Document has no wire value HERE, deliberately: this writer serves the LIVE runtime leaves
        // (an envelope's acting principal, a world.grant/world.revoke row), and a document principal means nothing
        // in the live grant table — the cross-document write-back channel reads its grants off the owner's
        // DOCUMENT (Server.WorldOwnedWorlds.Decide), never off the runtime table, so a live row for one would be
        // accepted-and-inert. The capability it names is authored with world.grant.set, which edits the document's
        // own Grants section through the ordered domain and the journal; that path is JSON-encoded and admits a
        // document principal on the row it authors. Group DOES carry a live wire value — unlike Document/World, a
        // group IS a legitimate grant TARGET on the runtime leaf: world.grant group:<id> ... round-trips through
        // this identical loopback codec path even for a local submission. A group never ACTS, so this value is
        // only ever reached as WorldGrant.Principal, never as an envelope's own actor.
        if (!WorldWireTags.TryToWire(
            value: principal.Kind,
            wire: out var kindWire
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(PrincipalKind)}.{principal.Kind} has no LIVE wire value — a {principal.Describe()} row is authored with world.grant.set (the document's Grants section, read by the cross-document write-back channel), never granted into the runtime table where nothing would read it"
            ));
        }
        writer.Write(value: kindWire);
        writer.Write(value: principal.Index);
        writer.Write(value: principal.Generation);
        WriteNullableString(
            writer,
            principal.Name
        );
    }
    // The rebuild leaf's own tagged union: one discriminant byte for WorldRebuildKind, the force flag, an optional
    // path hint, an optional content-hash pin, then — Load/Reload only — the embedded document through the document's
    // own canonical serializer (never a re-derived re-parse). Reset carries neither a path, a document, nor a
    // content-hash pin here: the base is server state, never client-supplied, and its CAS hash is computed at apply
    // time (WorldServer.ApplyRebuild), not known at submission — see ValidRebuildShape, checked on BOTH write and
    // read so a malformed request can never round-trip silently into a different shape than it claims.
    private static void WriteRebuild(BinaryWriter writer, WorldRebuildRequest request) {
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
        writer.Write(value: RebuildKindToWire(value: request.Kind));
        writer.Write(value: request.Force);
        WriteNullableString(
            writer,
            request.PathHint
        );
        WriteNullableString(
            writer,
            request.ContentHash
        );
        var hasDefinition = (request.Definition is not null);

        writer.Write(value: hasDefinition);
        if (hasDefinition) {
            var definition = request.Definition!;

            if (definition.Grants is { } grants) {
                foreach (var grant in grants) {
                    if (!TryValidatePrincipal(
                        grant.Principal,
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
            writer.Write(value: json.Length);
            writer.Write(buffer: json);
        }
    }
    private static void WriteRequiredString(BinaryWriter writer, string? value, string field) {
        if (string.IsNullOrEmpty(value: value)) {
            throw new LeafCodecException(failure: Fail(
                detail: $"{field} is null or empty",
                refusal: WorldCodecRefusal.PayloadMalformed
            ));
        }
        writer.Write(value: value);
    }
    // The screen-op leaf's own tagged union: one discriminant byte, then each case's own fields — mirroring the
    // addon-lifecycle leaf's shape (small, fixed, binary, no reflection-serialization debt). Insert never carries a
    // content hash on this wire — the receiving server reads and hashes ContentPath itself, at apply time, exactly
    // like a Reset request's base hash (see WorldServer.ApplyRebuild's own remarks).
    private static void WriteScreenOp(BinaryWriter writer, WorldScreenOp op) {
        if (op is null) {
            throw new LeafCodecException(failure: Fail(
                detail: "screen op is null",
                refusal: WorldCodecRefusal.PayloadMissing
            ));
        }
        switch (op) {
            case WorldScreenOp.Insert value:
                writer.Write(value: ((byte)0));
                writer.Write(value: value.Index);
                WriteRequiredString(
                    writer,
                    value.ContentPath,
                    "Insert.ContentPath"
                );
                WriteNullableString(
                    writer,
                    value.EngineId
                );
                WriteNullableString(
                    writer,
                    value.Options
                );
                break;
            case WorldScreenOp.Eject value:
                writer.Write(value: ((byte)1));
                writer.Write(value: value.Index);
                break;
            case WorldScreenOp.Select value:
                writer.Write(value: ((byte)2));
                writer.Write(value: value.Index);
                writer.Write(value: value.Entry);
                break;
            case WorldScreenOp.SetOptions value:
                writer.Write(value: ((byte)3));
                writer.Write(value: value.Index);
                WriteNullableString(
                    writer,
                    value.Options
                );
                break;
            case WorldScreenOp.Link value:
                writer.Write(value: ((byte)4));
                WriteRequiredString(
                    writer,
                    value.Name,
                    "Link.Name"
                );
                writer.Write(value: value.Members.Count);
                foreach (var member in value.Members) {
                    writer.Write(value: member);
                }
                break;
            case WorldScreenOp.Unlink value:
                writer.Write(value: ((byte)5));
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
    private static void WriteSection(BinaryWriter writer, WorldSection value) {
        if (!WorldWireTags.TryToWire(
            value: value,
            wire: out var wire
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(WorldSection)} value {((int)value)} is not declared"
            ));
        }
        writer.Write(value: wire);
    }
    private static void WriteSnapPose(BinaryWriter writer, WorldCommand.SnapPose value) {
        writer.Write(value: SnapPoseModeToWire(value: value.Mode));
        switch (value.Mode) {
            case SnapPoseMode.Pose:
                WriteVector(
                    writer,
                    value.Position
                ); writer.Write(value: value.YawRadians); writer.Write(value: value.PitchRadians); writer.Write(value: value.RollRadians); break;
            default:
                throw UnknownEnum(value: value.Mode);
        }
    }
    // Wire value 8 (formerly GrantSubjectKind.Table) is retired per the convention above — never reassign it.
    private static void WriteSubject(BinaryWriter writer, GrantSubject subject) {
        if (!WorldWireTags.TryToWire(
            value: subject.Kind,
            wire: out var kindWire
        )) {
            throw new LeafCodecException(failure: Fail(
                WorldCodecRefusal.EnumValueUnknown,
                $"{nameof(GrantSubjectKind)}.{subject.Kind} has no wire value"
            ));
        }
        writer.Write(value: kindWire);
        if (subject.Kind == GrantSubjectKind.Section) {
            WriteSection(
                value: ((WorldSection)subject.Value),
                writer: writer
            );
        } else {
            writer.Write(value: subject.Value);
        }
        WriteNullableString(
            writer,
            subject.Id
        );
    }
    private static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value: value.X); writer.Write(value: value.Y); writer.Write(value: value.Z); }

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
        TryReadStruct(
            bytes,
            reader => new WorldDesignation(
                EntityIndex: reader.ReadInt32(),
                Register: reader.ReadString(),
                Subject: ReadSubject(reader: reader),
                Point: (reader.ReadBoolean()
                    ? new FixedVector3(
                        X: FixedQ4816.FromRawBits(value: reader.ReadInt64()),
                        Y: FixedQ4816.FromRawBits(value: reader.ReadInt64()),
                        Z: FixedQ4816.FromRawBits(value: reader.ReadInt64())
                    )
                    : null)
            ),
            out designation,
            out failure
        );
    /// <summary>Decodes the grant leaf.</summary>
    public static bool TryDecodeGrant(ReadOnlySpan<byte> bytes, out WorldGrant grant, out WorldCodecFailure failure) =>
        TryReadStruct(
            bytes: bytes,
            failure: out failure,
            read: ReadGrant,
            value: out grant
        );
    /// <summary>Decodes the lever leaf.</summary>
    public static bool TryDecodeLever(ReadOnlySpan<byte> bytes, out WorldSessionLever lever, out WorldCodecFailure failure) =>
        TryReadStruct(
            bytes,
            reader => new WorldSessionLever(
                Section: ReadSection(reader: reader),
                Name: ReadLeverName(reader: reader),
                A: reader.ReadDouble(),
                B: reader.ReadDouble(),
                Seat: reader.ReadInt32()
            ),
            out lever,
            out failure
        );
    /// <summary>Decodes the mutation leaf under its stable catalog ordinal.</summary>
    public static bool TryDecodeMutation(ReadOnlySpan<byte> bytes, out WorldMutation? mutation, out WorldCodecFailure failure) {
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
            failure: out failure,
            mutation: mutation
        )) {
            mutation = null;
            return false;
        }
        return true;
    }
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
        TryReadStruct(
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
                writer.Write(value: designation.EntityIndex);
                writer.Write(value: (designation.Register ?? string.Empty));
                WriteSubject(
                    writer: writer,
                    subject: designation.Subject
                );
                writer.Write(value: designation.Point.HasValue);
                if (designation.Point is { } point) {
                    writer.Write(value: point.X.Value);
                    writer.Write(value: point.Y.Value);
                    writer.Write(value: point.Z.Value);
                }
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
                writer.Write(value: (lever.Name ?? string.Empty));
                writer.Write(value: lever.A);
                writer.Write(value: lever.B);
                writer.Write(value: lever.Seat);
            },
            out bytes,
            out failure
        );
    /// <summary>Encodes the mutation leaf under its stable catalog ordinal.</summary>
    public static bool TryEncodeMutation(WorldMutation mutation, out byte[] bytes, out WorldCodecFailure failure) {
        if (mutation is null) {
            bytes = [];
            failure = Fail(
                detail: "WorldMutation is null",
                refusal: WorldCodecRefusal.PayloadMissing
            );

            return false;
        }
        if (!TryValidateMutationPrincipals(
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
