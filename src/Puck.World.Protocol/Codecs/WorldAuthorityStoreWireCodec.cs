using Puck.Networking;

namespace Puck.World.Protocol;

/// <summary>What one authority journal entry records.</summary>
public enum WorldAuthorityJournalEntryKind : byte {
    /// <summary>A committed document mutation, encoded by the committed-mutation codec.</summary>
    Mutation = 0,

    /// <summary>A crossing record the authority wrote ahead of a crossing step, encoded by the crossing-log
    /// codec.</summary>
    Crossing = 1,
}
/// <summary>One authority journal entry — opaque encoded bytes (a committed mutation or a crossing record, opaque to
/// the store) plus the simulation tick and engine tick it was recorded at. The two clocks are independent:
/// <see cref="Tick"/> is the simulation-tick coordinate a replayed Cycle epoch rebases against,
/// <see cref="EngineTick"/> the engine-tick coordinate a replayed Advance epoch rebases against — never derived from
/// one another at any simulation rate.</summary>
/// <param name="Tick">The simulation tick the entry was recorded at.</param>
/// <param name="EngineTick">The engine tick the entry was recorded at.</param>
/// <param name="Encoded">The entry's own encoded bytes.</param>
/// <param name="Kind">What the entry records.</param>
public readonly record struct WorldAuthorityJournalEntry(ulong Tick, ulong EngineTick, ReadOnlyMemory<byte> Encoded, WorldAuthorityJournalEntryKind Kind = WorldAuthorityJournalEntryKind.Mutation);
/// <summary>Encodes and decodes the journal page <c>Puck.World.Server.WorldAuthorityBlobStore</c> writes beside the
/// checkpoint blob and names from its authority root: one page's sequence of entries. It uses the same bounded
/// <see cref="WireWriter"/>/<see cref="WireReader"/> discipline every peer decoder in this engine follows; its
/// magic-and-version pair refuses a foreign or future blob by name rather than misreading it.</summary>
public static class WorldAuthorityStoreWireCodec {
    // "PJNL" — Puck Journal.
    private const uint JournalMagic = 0x4C4E4A50U;
    private const ushort JournalVersion = 1;
    // A shape fingerprint is sixteen hex digits (FormatShapes); the bound leaves room for none else.
    private const int MaxFingerprintChars = 32;
    private const int MaxEntryBytes = ((8 * 1024) * 1024);

    /// <summary>Encodes one journal page's whole entry sequence.</summary>
    /// <param name="entries">The entries, in append order.</param>
    /// <returns>The page's raw bytes.</returns>
    public static byte[] EncodeJournalPage(IReadOnlyList<WorldAuthorityJournalEntry> entries) {
        var writer = new WireWriter();

        writer.WriteUInt32(value: JournalMagic);
        writer.WriteUInt32(value: JournalVersion);
        writer.WriteString(value: FormatShapes.WorldAuthorityStoreWireCodecJournalVersion);
        writer.WriteInt32(value: entries.Count);

        foreach (var entry in entries) {
            writer.WriteUInt64(value: entry.Tick);
            writer.WriteUInt64(value: entry.EngineTick);
            writer.WriteByte(value: ((byte)entry.Kind));
            writer.WriteBlock(value: entry.Encoded.Span);
        }

        return writer.ToArray();
    }
    /// <summary>Decodes one journal page's whole entry sequence.</summary>
    /// <param name="bytes">The page's raw bytes.</param>
    /// <param name="entries">The decoded entries on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the page decoded exactly.</returns>
    public static bool TryDecodeJournalPage(ReadOnlySpan<byte> bytes, out IReadOnlyList<WorldAuthorityJournalEntry> entries, out string reason) {
        var reader = new WireReader(bytes: bytes);
        var magic = reader.ReadUInt32();
        var version = reader.ReadUInt32();

        if (
            !reader.Failed &&
            (magic != JournalMagic)
        ) {
            reader.Fail(
                detail: $"journal magic {magic:x8} is not the journal magic",
                refusal: WireRefusal.PayloadMalformed
            );
        }
        if (
            !reader.Failed &&
            (version != JournalVersion)
        ) {
            reader.Fail(
                detail: $"journal version {version} is not the supported version {JournalVersion}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        var shape = reader.ReadRequiredString(
            field: "shape fingerprint",
            maxBytes: MaxFingerprintChars
        );

        if (
            !reader.Failed &&
            !string.Equals(
                a: shape,
                b: FormatShapes.WorldAuthorityStoreWireCodecJournalVersion,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            reader.Fail(
                detail: $"journal shape fingerprint {shape}, expected {FormatShapes.WorldAuthorityStoreWireCodecJournalVersion}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        var count = reader.ReadCount(
            field: "entry count",
            maximum: int.MaxValue,
            minimum: 0
        );
        var decoded = new WorldAuthorityJournalEntry[count];

        for (var index = 0; ((index < count) && !reader.Failed); index++) {
            var tick = reader.ReadUInt64();
            var engineTick = reader.ReadUInt64();
            var kind = reader.ReadByte();

            if (
                !reader.Failed &&
                (kind > ((byte)WorldAuthorityJournalEntryKind.Crossing))
            ) {
                reader.Fail(
                    detail: $"journal entry kind {kind} is not declared",
                    refusal: WireRefusal.EnumValueUnknown
                );
            }

            var encoded = reader.ReadBlock(
                field: "entry",
                maxBytes: MaxEntryBytes
            );

            decoded[index] = new WorldAuthorityJournalEntry(
                Encoded: encoded,
                EngineTick: engineTick,
                Kind: ((WorldAuthorityJournalEntryKind)kind),
                Tick: tick
            );
        }

        if (!reader.TryFinish(failure: out var failure)) {
            entries = [];
            reason = failure.ToString();

            return false;
        }

        entries = decoded;
        reason = string.Empty;

        return true;
    }
}
