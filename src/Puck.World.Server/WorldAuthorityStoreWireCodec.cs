using Puck.Networking;

namespace Puck.World.Server;

/// <summary>Encodes and decodes the two small wire shapes <see cref="WorldAuthorityBlobStore"/> owns beside the
/// checkpoint blob itself: the <c>checkpoints/latest</c> pointer, and one journal page's sequence of entries. Both
/// use the same bounded <see cref="WireWriter"/>/<see cref="WireReader"/> discipline every peer decoder in this
/// engine follows; a magic-and-version pair per shape refuses a foreign or future blob by name rather than
/// misreading it.</summary>
internal static class WorldAuthorityStoreWireCodec {
    // "PLTP" — Puck Latest Pointer.
    private const uint LatestPointerMagic = 0x50544C50U;
    private const ushort LatestPointerVersion = 1;
    // "PJNL" — Puck Journal.
    private const uint JournalMagic = 0x4C4E4A50U;
    private const ushort JournalVersion = 1;
    private const int MaxEntryBytes = ((8 * 1024) * 1024);
    private const int MaxHashChars = 128;

    /// <summary>Encodes the <c>checkpoints/latest</c> pointer.</summary>
    /// <param name="ordinal">The latest checkpoint's own ordinal.</param>
    /// <param name="tick">The engine tick the checkpoint was captured at.</param>
    /// <param name="hash">The checkpoint blob's own content-address pin.</param>
    /// <returns>The pointer's raw bytes.</returns>
    public static byte[] EncodeLatestPointer(long ordinal, ulong tick, string hash) {
        var writer = new WireWriter();

        writer.WriteUInt32(value: LatestPointerMagic);
        writer.WriteUInt32(value: LatestPointerVersion);
        writer.WriteInt64(value: ordinal);
        writer.WriteUInt64(value: tick);
        writer.WriteString(value: hash);

        return writer.ToArray();
    }
    /// <summary>Encodes one journal page's whole entry sequence.</summary>
    /// <param name="entries">The entries, in append order.</param>
    /// <returns>The page's raw bytes.</returns>
    public static byte[] EncodeJournalPage(IReadOnlyList<WorldMutationJournalEntry> entries) {
        var writer = new WireWriter();

        writer.WriteUInt32(value: JournalMagic);
        writer.WriteUInt32(value: JournalVersion);
        writer.WriteInt32(value: entries.Count);

        foreach (var entry in entries) {
            writer.WriteUInt64(value: entry.Tick);
            writer.WriteBlock(value: entry.Encoded.Span);
        }

        return writer.ToArray();
    }
    /// <summary>Decodes a <c>checkpoints/latest</c> pointer.</summary>
    /// <param name="bytes">The pointer's raw bytes.</param>
    /// <param name="ordinal">The decoded ordinal on success.</param>
    /// <param name="tick">The decoded tick on success.</param>
    /// <param name="hash">The decoded hash on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the pointer decoded exactly.</returns>
    public static bool TryDecodeLatestPointer(ReadOnlySpan<byte> bytes, out long ordinal, out ulong tick, out string hash, out string reason) {
        var reader = new WireReader(bytes: bytes);
        var magic = reader.ReadUInt32();
        var version = reader.ReadUInt32();

        if (
            !reader.Failed &&
            (magic != LatestPointerMagic)
        ) {
            reader.Fail(
                detail: $"pointer magic {magic:x8} is not the latest-pointer magic",
                refusal: WireRefusal.PayloadMalformed
            );
        }
        if (
            !reader.Failed &&
            (version != LatestPointerVersion)
        ) {
            reader.Fail(
                detail: $"pointer version {version} is not the supported version {LatestPointerVersion}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        ordinal = reader.ReadInt64();
        tick = reader.ReadUInt64();
        hash = reader.ReadRequiredString(
            field: "hash",
            maxBytes: MaxHashChars
        );

        if (!reader.TryFinish(failure: out var failure)) {
            ordinal = 0;
            tick = 0;
            hash = string.Empty;
            reason = failure.ToString();

            return false;
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Decodes one journal page's whole entry sequence.</summary>
    /// <param name="bytes">The page's raw bytes.</param>
    /// <param name="entries">The decoded entries on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the page decoded exactly.</returns>
    public static bool TryDecodeJournalPage(ReadOnlySpan<byte> bytes, out IReadOnlyList<WorldMutationJournalEntry> entries, out string reason) {
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

        var count = reader.ReadCount(
            field: "entry count",
            maximum: int.MaxValue,
            minimum: 0
        );
        var decoded = new WorldMutationJournalEntry[count];

        for (var index = 0; ((index < count) && !reader.Failed); index++) {
            var tick = reader.ReadUInt64();
            var encoded = reader.ReadBlock(
                field: "entry",
                maxBytes: MaxEntryBytes
            );

            decoded[index] = new WorldMutationJournalEntry(
                Encoded: encoded,
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
