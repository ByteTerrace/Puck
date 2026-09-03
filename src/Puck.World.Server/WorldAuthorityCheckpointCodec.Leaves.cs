using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteUInt16(WireWriter writer, ushort value) {
        writer.WriteByte(value: unchecked((byte)value));
        writer.WriteByte(value: unchecked((byte)(value >> 8)));
    }
    private static ushort ReadUInt16(ref WireReader reader) {
        var low = reader.ReadByte();
        var high = reader.ReadByte();

        return unchecked((ushort)(low | (high << 8)));
    }
    private static void WriteArray<T>(WireWriter writer, IReadOnlyList<T> items, Action<WireWriter, T> writeItem) {
        writer.WriteInt32(value: items.Count);

        foreach (var item in items) {
            writeItem(writer, item);
        }
    }
    private static T[] ReadArray<T>(ref WireReader reader, string field, ReadItem<T> readItem, int maximum = MaxCollectionCount) {
        var count = reader.ReadCount(
            field: field,
            maximum: maximum,
            minimum: 0
        );
        var items = new T[count];

        for (var index = 0; ((index < count) && !reader.Failed); index++) {
            items[index] = readItem(ref reader);
        }

        return items;
    }
    private static void WriteOptional<T>(WireWriter writer, T? value, Action<WireWriter, T> writeValue) where T : struct {
        writer.WriteBoolean(value: value.HasValue);

        if (value is { } present) {
            writeValue(writer, present);
        }
    }
    private static T? ReadOptional<T>(ref WireReader reader, ReadStructItem<T> readValue) where T : struct =>
        (reader.ReadBoolean()
            ? readValue(ref reader)
            : null
        );
    private static void WriteOptionalClass<T>(WireWriter writer, T? value, Action<WireWriter, T> writeValue) where T : class {
        writer.WriteBoolean(value: (value is not null));

        if (value is { } present) {
            writeValue(writer, present);
        }
    }
    private static T? ReadOptionalClass<T>(ref WireReader reader, ReadClassItem<T> readValue) where T : class =>
        (reader.ReadBoolean()
            ? readValue(ref reader)
            : null
        );
    private static void WriteULongArray(WireWriter writer, IReadOnlyList<ulong> values) => WriteArray(
        writer: writer,
        items: values,
        writeItem: static (w, v) => w.WriteUInt64(value: v)
    );
    private static ulong[] ReadULongArray(ref WireReader reader, string field) => ReadArray(
        reader: ref reader,
        field: field,
        readItem: static (ref WireReader r) => r.ReadUInt64()
    );
    private static void WriteLongArray(WireWriter writer, IReadOnlyList<long> values) => WriteArray(
        writer: writer,
        items: values,
        writeItem: static (w, v) => w.WriteInt64(value: v)
    );
    private static long[] ReadLongArray(ref WireReader reader, string field) => ReadArray(
        reader: ref reader,
        field: field,
        readItem: static (ref WireReader r) => r.ReadInt64()
    );
    private static void WriteBoolArray(WireWriter writer, IReadOnlyList<bool> values) => WriteArray(
        writer: writer,
        items: values,
        writeItem: static (w, v) => w.WriteBoolean(value: v)
    );
    private static bool[] ReadBoolArray(ref WireReader reader, string field, int maximum = MaxCollectionCount) => ReadArray(
        reader: ref reader,
        field: field,
        maximum: maximum,
        readItem: static (ref WireReader r) => r.ReadBoolean()
    );
    private static void WriteStringArray(WireWriter writer, IReadOnlyList<string> values) => WriteArray(
        writer: writer,
        items: values,
        writeItem: static (w, v) => w.WriteString(value: v)
    );
    private static string[] ReadStringArray(ref WireReader reader, string field) => ReadArray(
        reader: ref reader,
        field: field,
        readItem: (ref WireReader r) => r.ReadString(
            field: field,
            maxBytes: MaxStringBytes
        )
    );
    private static void WriteFixedArray(WireWriter writer, IReadOnlyList<FixedQ4816> values) => WriteArray(
        writer: writer,
        items: values,
        writeItem: static (w, v) => w.WriteFixed(value: v)
    );
    private static FixedQ4816[] ReadFixedArray(ref WireReader reader, string field) => ReadArray(
        reader: ref reader,
        field: field,
        readItem: static (ref WireReader r) => r.ReadFixed()
    );
    // ---- shared leaf types ----

    private static void WritePrincipal(WireWriter writer, WorldPrincipal principal) {
        if (!WorldWireCodec.TryWritePrincipal(
            principal: principal,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"{nameof(PrincipalKind)}.{principal.Kind} has no live wire value");
        }
    }
    private static WorldPrincipal ReadPrincipal(ref WireReader reader) {
        var declared = WorldWireCodec.TryReadPrincipal(
            kindWire: out var kindWire,
            nameField: "principal name",
            principal: out var principal,
            reader: ref reader
        );

        if (
            !declared &&
            !reader.Failed
        ) {
            reader.Fail(
                detail: $"{nameof(PrincipalKind)} wire value {kindWire} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        return principal;
    }
    private static void WriteSubject(WireWriter writer, GrantSubject subject) {
        if (!WorldWireTags.TryToWire(
            value: subject.Kind,
            wire: out var kindWire
        )) {
            throw new InvalidOperationException(message: $"{nameof(GrantSubjectKind)}.{subject.Kind} has no wire value");
        }
        writer.WriteByte(value: kindWire);
        writer.WriteInt32(value: subject.Value);
        writer.WriteNullableString(value: subject.Id);
    }
    private static GrantSubject ReadSubject(ref WireReader reader) {
        var kindWire = reader.ReadByte();
        var kindValid = WorldWireTags.TryFromWire(
            value: out GrantSubjectKind kind,
            wire: kindWire
        );

        if (
            !reader.Failed &&
            !kindValid
        ) {
            reader.Fail(
                detail: $"{nameof(GrantSubjectKind)} wire value {kindWire} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var value = reader.ReadInt32();
        var id = reader.ReadNullableString(
            field: "subject id",
            maxBytes: MaxStringBytes
        );

        return new GrantSubject(
            Id: id,
            Kind: kind,
            Value: value
        );
    }
    private static void WriteIdentityProjection(WireWriter writer, WorldIdentityProjection projection) {
        writer.WriteString(value: projection.Id);
        writer.WriteString(value: projection.Name);
        writer.WriteString(value: projection.ColorHex);
        writer.WriteNullableFixed(value: projection.MoveSpeed);
        writer.WriteNullableFixed(value: projection.TurnSpeed);
    }
    private static WorldIdentityProjection ReadIdentityProjection(ref WireReader reader) => new(
        Id: reader.ReadString(
            field: "identity id",
            maxBytes: MaxStringBytes
        ),
        Name: reader.ReadString(
            field: "identity name",
            maxBytes: MaxStringBytes
        ),
        ColorHex: reader.ReadString(
            field: "identity color",
            maxBytes: MaxStringBytes
        ),
        MoveSpeed: reader.ReadNullableFixed(),
        TurnSpeed: reader.ReadNullableFixed()
    );
    // A traveler/committed-member identity is carried across a checkpoint restore through the identical reduction a
    // federated crossing already applies (WorldIdentity.Project()/FromProjection) — a body's own simulation never
    // reads a Document/Bindings/Hud/SeatLook off an in-flight traveler's identity, only its projection (Name/Color/
    // MoveSpeed/TurnSpeed), so this is the SAME exclusion §3.1's rule already grants Server.WorldPopulation's own
    // Profile field (see WorldPopulationEntryCheckpoint.Profile), extended to escrow's own identity-carrying rows.
    private static void WriteIdentityOptional(WireWriter writer, WorldIdentity? identity) => WriteOptional(
        writer: writer,
        value: identity?.Project(),
        writeValue: WriteIdentityProjection
    );
    private static WorldIdentity? ReadIdentityOptional(ref WireReader reader, WorldPlayerDefaults defaults) {
        var projection = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadIdentityProjection(reader: ref r)
        );

        return ((projection is { } value)
            ? WorldIdentity.FromProjection(
                defaults: defaults,
                projection: in value
            )
            : null
        );
    }
    private static void WriteIntentSource(WireWriter writer, IntentSource source) {
        if (!WorldWireCodec.TryWriteIntentSource(
            source: source,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"{nameof(IntentSource)} '{source}' has no live wire value");
        }
    }
    private static IntentSource ReadIntentSource(ref WireReader reader) {
        if (!WorldWireCodec.TryReadIntentSource(
            producerNameField: "producer name",
            reader: ref reader,
            source: out var source,
            wire: out var kind
        )) {
            if (!reader.Failed) {
                reader.Fail(
                    detail: $"{nameof(IntentSource)} wire value {kind} is not declared",
                    refusal: WireRefusal.EnumValueUnknown
                );
            }

            return IntentSource.Live;
        }

        return source;
    }
    private static void WritePeerEventEntry(WireWriter writer, WorldPeerEventEntry peer) {
        writer.WriteInt32(value: peer.BodyIndex);
        writer.WriteInt32(value: peer.Generation);
        WriteIntentSource(
            writer: writer,
            source: peer.Source
        );
        WritePrincipal(
            writer: writer,
            principal: peer.Identity
        );
        writer.WriteString(value: peer.IdentityDomain);
        writer.WriteString(value: peer.IdentitySubject);
        writer.WriteBoolean(value: peer.AuthorityTransferred);
        writer.WriteNullableString(value: peer.PlacementId);
        writer.WriteByte(value: peer.CatalogRig);
    }
    private static WorldPeerEventEntry ReadPeerEventEntry(ref WireReader reader) {
        var bodyIndex = reader.ReadInt32();
        var generation = reader.ReadInt32();
        var source = ReadIntentSource(reader: ref reader);
        var identity = ReadPrincipal(reader: ref reader);
        var identityDomain = reader.ReadString(
            field: "peer identity domain",
            maxBytes: MaxStringBytes
        );
        var identitySubject = reader.ReadString(
            field: "peer identity subject",
            maxBytes: MaxStringBytes
        );
        var authorityTransferred = reader.ReadBoolean();
        var placementId = reader.ReadNullableString(
            field: "peer placement id",
            maxBytes: MaxStringBytes
        );
        var catalogRig = reader.ReadByte();

        return new WorldPeerEventEntry(
            AuthorityTransferred: authorityTransferred,
            BodyIndex: bodyIndex,
            CatalogRig: catalogRig,
            Generation: generation,
            Identity: identity,
            IdentityDomain: identityDomain,
            IdentitySubject: identitySubject,
            PlacementId: placementId,
            Source: source
        );
    }
    private static void WriteAdmissionGrant(WireWriter writer, WorldAdmissionGrant grant) {
        WriteCapability(
            capability: grant.Capability,
            writer: writer
        );
        WriteOptional(
            writer: writer,
            value: grant.Subject,
            writeValue: WriteSubject
        );
        writer.WriteBoolean(value: grant.Exclusive);
        WriteOptional(
            writer: writer,
            value: grant.Budget,
            writeValue: static (w, v) => w.WriteInt32(value: v)
        );
        WriteOptional(
            writer: writer,
            value: grant.EventBudget,
            writeValue: static (w, v) => w.WriteInt32(value: v)
        );
        WriteOptional(
            writer: writer,
            value: grant.KindMask,
            writeValue: static (w, v) => WriteUInt128(
                writer: w,
                value: v.Bits
            )
        );
    }
    private static WorldAdmissionGrant ReadAdmissionGrant(ref WireReader reader) {
        var capability = ReadCapability(reader: ref reader);
        var subject = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadSubject(reader: ref r)
        );
        var exclusive = reader.ReadBoolean();
        var budget = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ((ushort)r.ReadInt32())
        );
        var eventBudget = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ((ushort)r.ReadInt32())
        );
        var kindMask = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => new MutationKindMask(Bits: ReadUInt128(reader: ref r))
        );

        return new WorldAdmissionGrant(
            Budget: budget,
            Capability: capability,
            EventBudget: eventBudget,
            Exclusive: exclusive,
            KindMask: kindMask,
            Subject: subject
        );
    }
    // Mirrors WorldSubmissionCodec.WriteKindMaskBits: two 64-bit halves, low half first — BinaryWriter/WireWriter
    // carry no native UInt128 lane, so writing it as one 64-bit value would silently drop every bit above 63.
    private static void WriteUInt128(WireWriter writer, UInt128 value) {
        writer.WriteUInt64(value: ((ulong)value));
        writer.WriteUInt64(value: ((ulong)(value >> 64)));
    }
    private static UInt128 ReadUInt128(ref WireReader reader) {
        var low = reader.ReadUInt64();
        var high = reader.ReadUInt64();

        return (((UInt128)high) << 64) | low;
    }
    // Reuses the same leaf shape the "Grant"/"Revoke" tape entries already encode a WorldGrant with
    // (WorldSubmissionCodec.TryEncodeGrant/TryDecodeGrant) rather than re-deriving the field list here — the leaf
    // owns the definitive layout, this call site only frames the resulting bytes as one checkpoint block.
    private static void WriteWorldGrant(WireWriter writer, WorldGrant grant) {
        if (!WorldSubmissionCodec.TryEncodeGrant(
            bytes: out var bytes,
            failure: out var failure,
            grant: grant
        )) {
            throw new InvalidOperationException(message: $"checkpoint grant failed to encode — {failure}");
        }

        writer.WriteBlock(value: bytes);
    }
    private static WorldGrant ReadWorldGrant(ref WireReader reader) {
        var bytes = reader.ReadBlock(
            field: "grant",
            maxBytes: MaxSectionBytes
        );

        if (reader.Failed) {
            return default;
        }
        if (!WorldSubmissionCodec.TryDecodeGrant(
            bytes: bytes,
            failure: out var failure,
            grant: out var grant
        )) {
            reader.Fail(
                detail: $"grant: {failure}",
                refusal: WireRefusal.PayloadMalformed
            );

            return default;
        }

        return grant;
    }
    private static void WriteAdmissionVerdict(WireWriter writer, WorldAdmissionVerdict verdict) {
        writer.WriteString(value: verdict.IdentityDomain);
        writer.WriteString(value: verdict.IdentitySubject);
        WriteArray(
            writer: writer,
            items: verdict.Templates,
            writeItem: WriteAdmissionGrant
        );
        writer.WriteByte(value: ((byte)verdict.Tier));
    }
    private static WorldAdmissionVerdict ReadAdmissionVerdict(ref WireReader reader) {
        var domain = reader.ReadString(
            field: "verdict identity domain",
            maxBytes: MaxStringBytes
        );
        var subject = reader.ReadString(
            field: "verdict identity subject",
            maxBytes: MaxStringBytes
        );
        var templates = ReadArray(
            reader: ref reader,
            field: "verdict templates",
            readItem: static (ref WireReader r) => ReadAdmissionGrant(reader: ref r)
        );
        var tier = ((WorldDisclosureTier)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: tier)
        ) {
            reader.Fail(
                detail: $"{nameof(WorldDisclosureTier)} wire value {((byte)tier)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        return WorldAdmissionVerdict.Restore(
            identityDomain: domain,
            identitySubject: subject,
            templates: templates,
            tier: tier
        );
    }

    // ---- leaf reuse: WorldSubmissionCodec's own mutation/rebuild/addon-lifecycle leaves ----

    private delegate bool TryEncodeLeaf<T>(T value, out byte[] bytes, out WorldCodecFailure failure);
    private delegate bool TryDecodeLeaf<T>(ReadOnlySpan<byte> bytes, out T? value, out WorldCodecFailure failure) where T : class;
}
