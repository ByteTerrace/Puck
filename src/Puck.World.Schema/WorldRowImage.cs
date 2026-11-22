using System.Buffers;
using Puck.Abstractions;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Memory;
using Puck.Assets;

namespace Puck.World;

/// <summary>One immutable observable row: the cells a recipient may read and the row's observation, as
/// <see cref="WorldRowImageWriter"/> reads them from the store under one disclosure mask. Vector payloads are privately
/// copied.</summary>
public sealed class WorldRecipientImage {
    internal WorldRecipientImage(Sha256Digest digest, StateCell[] cells, WorldObservedRow? observation, int bytes) {
        Digest = digest; Cells = Array.AsReadOnly(cells); Observation = observation; Bytes = bytes;
    }
    // Spelled on first read: most images are compared by digest alone. A pin is one reference, so a racing first read
    // writes it whole and every writer writes the same text.
    private ContentPin m_identity;
    /// <summary>Content identity, including counts, placeholder order, traits and observations.</summary>
    public ContentPin Identity {
        get {
            if (m_identity == default) {
                Span<byte> digest = stackalloc byte[Sha256Digest.SizeInBytes];

                Digest.CopyTo(destination: digest);
                m_identity = ContentPin.FromDigest(digest: digest);
            }
            return m_identity;
        }
    }
    /// <summary>Readable cells in recipient order.</summary>
    public IReadOnlyList<StateCell> Cells { get; }
    /// <summary>The presentation image, or null when the row itself is withheld.</summary>
    public WorldObservedRow? Observation { get; }
    internal int Bytes { get; }
    // The SHA-256 digest of the image's canonical bytes (WorldRowImageWriter.Write), which Identity spells.
    internal Sha256Digest Digest { get; }
}

// The keys a row held, in order, when a stream last read the row whole: all a delivery cursor keeps of such a row, to
// tell which cells the stream read if the row later withholds. One list serves every stream that read the row.
internal sealed class WorldRowMembers(CellName[] keys) {
    public static WorldRowMembers Empty { get; } = new(keys: []);
    public ReadOnlySpan<CellName> Keys => keys;
}

// How a row reaches one audience: not at all (its ceiling carries no such row), whole (nothing on the row's path can
// withhold a cell from it), or cell by cell.
internal enum WorldRowReading : byte {
    Absent,
    Whole,
    Restricted,
}

// Everything about an audience that decides what it is disclosed of one row: how the row reaches it and, for a row
// read cell by cell, whether the row's own policy admits it and whether it may read each held cell, one bit a cell in
// StateArena.TryNextCell order. Bits[0] holds the row answer and the cell bits follow. Two audiences whose masks for a
// row are equal are disclosed the same image of it, because the image is read from the store and the mask alone.
internal readonly struct WorldRowMask(WorldRowReading reading, byte[] bits, int length) : WorldStateDisclosure.IRowReader {
    public static WorldRowMask Absent => new(reading: WorldRowReading.Absent, bits: [], length: 0);
    public static WorldRowMask Whole => new(reading: WorldRowReading.Whole, bits: [], length: 0);

    public WorldRowReading Reading => reading;
    public ReadOnlySpan<byte> Bytes => bits.AsSpan(start: 0, length: length);
    public bool ReadsRow => ((reading == WorldRowReading.Whole) || ((length > 0) && (bits[0] != 0)));
    public bool ReadsCell(int index) => ((reading == WorldRowReading.Whole) ||
        (((1 + (index >> 3)) < length) && (((bits[(1 + (index >> 3))] >> (index & 7)) & 1) != 0)));

    bool WorldStateDisclosure.IRowReader.ReadsRow(StateRow row) => ReadsRow;
    bool WorldStateDisclosure.IRowReader.ReadsCell(StateRow row, int rowOrdinal, CellKey key, int index) => ReadsCell(index: index);

    // Asks the observer everything a cell-by-cell reading of the row depends on, into scratch; returns the mask's length.
    public static int Take(ref WorldStateDisclosure.Observer observer, StateArena arena, int row, ref byte[] scratch) {
        var declaration = arena.Rows[row];
        var readsRow = observer.Allows(policy: declaration.Visibility);
        var cursor = 0;
        var index = 0;

        ArrayCapacity.EnsureLength(array: ref scratch, required: 1);
        scratch[0] = (readsRow ? ((byte)1) : ((byte)0));
        while (arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: row)) {
            var at = (1 + (index >> 3));

            if ((index & 7) == 0) {
                ArrayCapacity.EnsureLength(array: ref scratch, required: (at + 1));
                scratch[at] = 0;
            }
            if (readsRow && observer.CanRead(key: key, row: declaration, rowOrdinal: row)) { scratch[at] |= ((byte)(1 << (index & 7))); }
            index++;
        }
        return (1 + ((index + 7) >> 3));
    }
}

// The one reader of a row for disclosure. Write and Materialize are both functions of the store's row and a mask, and
// of nothing else about an audience: Write appends the canonical bytes of what the mask discloses, whose digest is the
// image's identity, and Materialize builds the image those bytes describe. They visit the row's held cells in the same
// order and make the same three decisions for each (whether it is a replica cell, an observed cell or an anonymous
// back), so a value Materialize puts in an image is one Write wrote.
internal static class WorldRowImageWriter {
    private const int ReplicaCell = 1;
    private const int ObservedCell = 2;
    private const int HiddenBack = 4;
    private const int DeclaredCells = 8;

    // Whether the row's observation crosses under the mask: a host-owned row has none, and a row's own policy decides
    // for the rest.
    private static bool Observes(StateRow declaration, in WorldRowMask mask) => (!declaration.HostOwned && mask.ReadsRow);

    // Appends the image's canonical bytes; keys, when given, takes the ordinal of each replica cell's key, which a
    // key-change proof tests a later write against.
    public static void Write(IBufferWriter<byte> bytes, StateArena arena, int row, in WorldRowMask mask, List<int>? keys) {
        if (mask.Reading == WorldRowReading.Absent) {
            bytes.WriteByte(value: 0);

            return;
        }

        var declaration = arena.Rows[row];
        var whole = (mask.Reading == WorldRowReading.Whole);
        var observes = Observes(declaration: declaration, mask: in mask);
        var hiddenPolicy = (declaration.Visibility?.Hidden ?? HiddenCells.Omit);
        var hidden = 0;

        bytes.WriteByte(value: ((byte)(1 | (observes ? 2 : 0))));
        if (arena.StoresCells(rowOrdinal: row)) {
            var cursor = 0;
            var index = 0;

            while (arena.TryNextCellSlot(cursor: ref cursor, key: out var key, rowOrdinal: row, slot: out var slot)) {
                var holds = arena.SlotHoldsValue(slot: slot);
                var reads = mask.ReadsCell(index: index);
                var tag = ((whole || (holds && reads)) ? ReplicaCell : 0) | ((observes && holds && reads) ? ObservedCell : 0);

                if (observes && holds && !reads && (hiddenPolicy != HiddenCells.Omit)) {
                    hidden++;
                    if (hiddenPolicy == HiddenCells.Placeholder) { tag |= HiddenBack; }
                }
                if (tag != 0) {
                    bytes.WriteByte(value: ((byte)tag));
                    if ((tag & (ReplicaCell | ObservedCell)) != 0) { arena.WriteCell(rowOrdinal: row, slot: slot, writer: bytes); }
                    if ((tag & ReplicaCell) != 0) { keys?.Add(item: key.Ordinal); }
                }
                index++;
            }
        } else if (whole && (declaration.Cells is { Count: > 0 })) {
            // A row the arena does not store carries its declared cells, which never change while the arena stands.
            bytes.WriteByte(value: DeclaredCells);
            bytes.WriteBytes(value: CanonicalJsonDocument.SerializeCompact(
                ((declaration as WorldStateRow) ?? new WorldStateRow(declaration, false, null)), WorldJsonContext.Default.WorldStateRow));
        }
        bytes.WriteByte(value: 0);
        if (!observes) { return; }
        bytes.WriteCodeUnits(value: declaration.Name.Value);
        bytes.WriteByte(value: ((byte)declaration.Kind));
        bytes.WriteByte(value: (declaration.IsSlot ? ((byte)1) : ((byte)0)));
        bytes.WriteVarUInt(value: ((uint)hidden));
        WriteOptional(bytes: bytes, present: ((declaration.Kind == CellKind.Vector) && (declaration.Space is not null)));
        if ((declaration.Kind == CellKind.Vector) && (declaration.Space is { } space)) { bytes.WriteCodeUnits(value: space); }
        WriteOptional(bytes: bytes, present: (declaration.Min is not null));
        if (declaration.Min is { } min) { bytes.WriteUInt64(value: ((ulong)min)); }
        WriteOptional(bytes: bytes, present: (declaration.Max is not null));
        if (declaration.Max is { } max) { bytes.WriteUInt64(value: ((ulong)max)); }
        WriteOptional(bytes: bytes, present: (declaration.Capacity is not null));
        if (declaration.Capacity is { } capacity) { bytes.WriteUInt64(value: ((ulong)capacity)); }
        // An observed cell carries the trait that governs it, which is its own or, failing that, the row's.
        StateArena.WriteBehavior(behavior: arena.Catalog.DefaultBehavior(rowOrdinal: row), writer: bytes);
    }
    private static void WriteOptional(IBufferWriter<byte> bytes, bool present) => bytes.WriteByte(value: (present ? ((byte)1) : ((byte)0)));

    // Builds the image the mask discloses. export is the row's live export (StateArena.ToRow), whose cells stand in
    // the order the store visits them; null for an absent row.
    public static WorldRecipientImage Materialize(StateArena arena, int row, in WorldRowMask mask, StateRow? export, Sha256Digest digest, int bytes) {
        if ((mask.Reading == WorldRowReading.Absent) || (export is null)) { return new WorldRecipientImage(digest: digest, cells: [], observation: null, bytes: bytes); }

        var declaration = arena.Rows[row];
        var whole = (mask.Reading == WorldRowReading.Whole);
        var exported = (export.Cells ?? []);
        var cells = new List<StateCell>(capacity: exported.Count);

        if (arena.StoresCells(rowOrdinal: row)) {
            var cursor = 0;

            for (var index = 0; (index < exported.Count); index++) {
                if (!arena.TryNextCellSlot(cursor: ref cursor, key: out _, rowOrdinal: row, slot: out var slot)) {
                    throw new InvalidOperationException(message: "A row's export holds a cell the store does not visit.");
                }
                if (whole || (arena.SlotHoldsValue(slot: slot) && mask.ReadsCell(index: index))) { cells.Add(item: Detached(cell: exported[index])); }
            }
        } else if (whole) {
            foreach (var cell in exported) { cells.Add(item: Detached(cell: cell)); }
        }

        var reader = mask;
        var observation = (Observes(declaration: declaration, mask: in mask)
            ? WorldStateDisclosure.ComposeRow(arena: arena, presented: true, reader: ref reader, row: declaration, rowOrdinal: row)
            : null);

        if (observation is not null) { observation = observation with { Cells = Array.AsReadOnly(array: observation.Cells.ToArray()) }; }
        return new WorldRecipientImage(digest: digest, cells: [.. cells], observation: observation, bytes: bytes);
    }
    private static StateCell Detached(StateCell cell) => ((cell.Value.Kind == CellKind.Vector) ? (cell with { Value = cell.Value.DetachedCopy() }) : cell);
}
