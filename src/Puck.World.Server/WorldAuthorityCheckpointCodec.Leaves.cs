using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteULongArray(WireWriter writer, IReadOnlyList<ulong> values) => writer.WriteArray(
        items: values,
        writeItem: static (w, v) => w.WriteUInt64(value: v)
    );
    private static ulong[] ReadULongArray(ref WireReader reader, string field) => reader.ReadArray(
        field: field,
        readItem: static (ref WireReader r) => r.ReadUInt64(),
        maximum: MaxCollectionCount
    );
    private static void WriteLongArray(WireWriter writer, IReadOnlyList<long> values) => writer.WriteArray(
        items: values,
        writeItem: static (w, v) => w.WriteInt64(value: v)
    );
    private static long[] ReadLongArray(ref WireReader reader, string field) => reader.ReadArray(
        field: field,
        readItem: static (ref WireReader r) => r.ReadInt64(),
        maximum: MaxCollectionCount
    );
    private static void WriteIntArray(WireWriter writer, IReadOnlyList<int> values) => writer.WriteArray(
        items: values,
        writeItem: static (w, v) => w.WriteInt32(value: v)
    );
    private static int[] ReadIntArray(ref WireReader reader, string field) => reader.ReadArray(
        field: field,
        readItem: static (ref WireReader r) => r.ReadInt32(),
        maximum: MaxCollectionCount
    );
    private static void WriteBoolArray(WireWriter writer, IReadOnlyList<bool> values) => writer.WriteArray(
        items: values,
        writeItem: static (w, v) => w.WriteBoolean(value: v)
    );
    private static bool[] ReadBoolArray(ref WireReader reader, string field, int maximum = MaxCollectionCount) => reader.ReadArray(
        field: field,
        maximum: maximum,
        readItem: static (ref WireReader r) => r.ReadBoolean()
    );
    private static void WriteStringArray(WireWriter writer, IReadOnlyList<string> values) => writer.WriteArray(
        items: values,
        writeItem: static (w, v) => w.WriteString(value: v)
    );
    private static string[] ReadStringArray(ref WireReader reader, string field) => reader.ReadArray(
        field: field,
        readItem: (ref WireReader r) => r.ReadString(
            field: field,
            maxBytes: MaxStringBytes
        ),
        maximum: MaxCollectionCount
    );
    private static void WriteFixedArray(WireWriter writer, IReadOnlyList<FixedQ4816> values) => writer.WriteArray(
        items: values,
        writeItem: static (w, v) => w.WriteFixed(value: v)
    );
    private static FixedQ4816[] ReadFixedArray(ref WireReader reader, string field) => reader.ReadArray(
        field: field,
        readItem: static (ref WireReader r) => r.ReadFixed(),
        maximum: MaxCollectionCount
    );
    // ---- shared leaf types ----

    private static void WritePrincipal(WireWriter writer, Principal principal) {
        if (!WorldWireCodec.TryWritePrincipal(
            principal: principal,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"{nameof(PrincipalKind)}.{principal.Kind} has no live wire value");
        }
    }
    private static void WriteGrantee(WireWriter writer, Grantee grantee) {
        if (!WorldWireCodec.TryWriteGrantee(
            grantee: grantee,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"{grantee.Describe()} has no live wire value");
        }
    }
    private static void WriteSubject(WireWriter writer, GrantSubject subject) {
        if (!WorldWireCodec.TryWriteSubject(
            subject: subject,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"{nameof(GrantSubject)} {subject.Kind}:{subject.Value} has no wire value");
        }
    }
    private static void WriteIntentSource(WireWriter writer, IntentSource source) {
        if (!WorldWireCodec.TryWriteIntentSource(
            source: source,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"{nameof(IntentSource)} '{source}' has no live wire value");
        }
    }
    private static void WritePeerEventEntry(WireWriter writer, WorldPeerEventEntry peer) {
        if (!WorldWireLeaves.TryWritePeerEventEntry(
            peer: peer,
            writer: writer
        )) {
            throw new InvalidOperationException(message: $"peer event entry {peer.Identity.Describe()} (source '{peer.Source}') has no live wire value");
        }
    }

    internal static void WriteAdmissionGrant(WireWriter writer, WorldAdmissionGrant grant) {
        WriteCapability(
            capability: grant.Capability,
            writer: writer
        );
        writer.WriteOptional(
            value: grant.Subject,
            writeValue: WriteSubject
        );
        writer.WriteBoolean(value: grant.Exclusive);
        writer.WriteOptional(
            value: grant.Budget,
            writeValue: static (w, v) => w.WriteInt32(value: v)
        );
        writer.WriteOptional(
            value: grant.EventBudget,
            writeValue: static (w, v) => w.WriteInt32(value: v)
        );
        writer.WriteOptional(
            value: grant.KindMask,
            writeValue: static (w, v) => w.WriteUInt128(
                value: v.Bits
            )
        );
    }
    internal static WorldAdmissionGrant ReadAdmissionGrant(ref WireReader reader) {
        var capability = WorldWireCodec.ReadCapability(reader: ref reader);
        var subject = reader.ReadOptional(
            readValue: static (ref WireReader r) => WorldWireCodec.ReadSubject(reader: ref r)
        );
        var exclusive = reader.ReadBoolean();
        var budget = reader.ReadOptional(
            readValue: static (ref WireReader r) => ((ushort)r.ReadInt32())
        );
        var eventBudget = reader.ReadOptional(
            readValue: static (ref WireReader r) => ((ushort)r.ReadInt32())
        );
        var kindMask = reader.ReadOptional(
            readValue: static (ref WireReader r) => new MutationKindMask(Bits: r.ReadUInt128())
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
        writer.WriteArray(
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
        var templates = reader.ReadArray(
            field: "verdict templates",
            readItem: static (ref WireReader r) => ReadAdmissionGrant(reader: ref r),
            maximum: MaxCollectionCount
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
