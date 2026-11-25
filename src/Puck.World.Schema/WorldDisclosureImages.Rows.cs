using Puck.Maths;
using System.Buffers;
using Puck.Abstractions;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Memory;
using Puck.Assets;

namespace Puck.World;

public sealed partial class WorldDisclosureImages {
    /// <summary>One view's audience as the table keys it. Only <see cref="Audience"/> makes one, from the view's own
    /// answers after the table is brought current to the view's store, so a row read through it is read for that view
    /// and no other: a caller cannot pair one view with another's key. A key taken before the store moved is taken
    /// again where it is used.</summary>
    internal readonly struct AudienceKey {
        private readonly StateArena m_arena;
        private readonly long m_revision;
        private readonly long m_moves;

        private AudienceKey(WorldRecipientView view, string text) {
            View = view; Text = text; m_arena = view.Arena; m_revision = m_arena.DisclosureRevision; m_moves = m_arena.MoveSequence;
        }
        /// <summary>Gets the view the key was taken for.</summary>
        public WorldRecipientView View { get; }
        /// <summary>Gets the key: the view's ceiling, its authority and its answer to each policy the store holds.</summary>
        public string Text { get; }
        // Whether nothing an answer was read from has moved: the same store, at the same row moves and policy revision.
        internal bool Stands => (ReferenceEquals(View.Arena, m_arena) && (m_arena.DisclosureRevision == m_revision) && (m_arena.MoveSequence == m_moves));
        internal static AudienceKey Of(WorldDisclosureImages table, WorldRecipientView view) => new(view: view, text: table.AudienceText(view));
    }
    /// <summary>Takes a view's audience key, bringing the table current to the view's store first.</summary>
    internal AudienceKey Audience(WorldRecipientView view) => AudienceKey.Of(table: this, view: view);
    private AudienceKey Fresh(AudienceKey audience) => (audience.Stands ? audience : Audience(audience.View));

    // How a row reaches one audience: not at all (its ceiling carries no such row), whole (nothing on the row's path
    // can withhold a cell from it), or cell by cell.
    private enum RowReading : byte {
        Absent,
        Whole,
        Restricted,
    }
    // Everything about an audience that decides what it is disclosed of one row: how the row reaches it and, for a row
    // read cell by cell, whether the row's own policy admits it and whether it may read each held cell, one bit a cell
    // in StateArena.TryNextCell order. Bits[0] holds the row answer and the cell bits follow. Two audiences whose masks
    // for a row are equal are disclosed the same image of it, because Pass reads the store and the mask alone. Only
    // this table can make one, and only Reading and Unsettled ask an observer for one (Take).
    private readonly struct RowMask(RowReading reading, byte[] bits, int length) : WorldStateDisclosure.IRowReader {
        public static RowMask Absent => new(reading: RowReading.Absent, bits: [], length: 0);
        public static RowMask Whole => new(reading: RowReading.Whole, bits: [], length: 0);

        public RowReading Reading => reading;
        public ReadOnlySpan<byte> Bytes => bits.AsSpan(start: 0, length: length);
        public bool ReadsRow => ((reading == RowReading.Whole) || ((length > 0) && (bits[0] != 0)));

        bool WorldStateDisclosure.IRowReader.ReadsRow(StateRow row) => ReadsRow;
        bool WorldStateDisclosure.IRowReader.ReadsCell(StateRow row, int rowOrdinal, CellKey key, int index) => ((reading == RowReading.Whole) ||
            (((1 + (index >> 3)) < length) && (((bits[(1 + (index >> 3))] >> (index & 7)) & 1) != 0)));

        // Asks the observer everything a cell-by-cell reading of the row depends on, into scratch.
        public static RowMask Take(ref WorldStateDisclosure.Observer observer, StateArena arena, int row, ref byte[] scratch) {
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
            return new RowMask(reading: RowReading.Restricted, bits: scratch, length: (1 + index.CeilingDivide(divisor: 8)));
        }
    }

    // One row as every audience with one mask is disclosed it. Version is the row version its digest was last taken
    // at; a reader holds it current through an Entry or, for a row read whole, by the row's version alone. The mask
    // never changes, so the composition is written again in place when its row moves.
    private sealed class Composition(RowReading reading, byte[] mask, ulong maskHash) {
        public RowMask Mask => new(reading: reading, bits: mask, length: mask.Length);
        public ulong MaskHash { get; } = maskHash;
        public bool Composed { get; set; }
        public ulong Version { get; set; }
        public Sha256Digest Digest { get; set; }
        // The image built from the store under the mask, once a delivery asked for it; dropped at every write.
        public WorldRecipientImage? Image { get; set; }
        // The ordinals of the keys of its replica cells, which a key-change proof tests a write against.
        public int[] Keys = [];
        public int KeyCount { get; set; }
        // The disclosure window that last looked it up, for letting go of a mask no audience answers with any more.
        public long Window { get; set; }
    }
    // One audience's reading of a row it reads cell by cell: the composition its mask named, and the row and dependency
    // versions the mask was taken at. While both stand, the mask an observer would answer is the one taken then.
    private sealed class Entry {
        public ulong Version;
        public ulong Dependencies;
        public Composition Composition = null!;
    }

    private const byte DeclaredCells = 8;

    // The one pass over a row for disclosure: it reads the store and a mask, and nothing else about an audience. It
    // writes the canonical bytes of what the mask discloses into bytes, answers their digest and, when build is set,
    // the image those bytes describe, whose cells the same walk collected (WorldStateDisclosure.VisitRow). The
    // image carries the digest of the bytes written beside it, so an image and its digest cannot disagree.
    private static WorldRecipientImage? Pass(ArrayBufferWriter<byte> bytes, StateArena arena, int row, RowMask mask, List<int>? keys, StateRow? export, bool build,
        out Sha256Digest digest) {
        bytes.ResetWrittenCount();
        keys?.Clear();
        if (mask.Reading == RowReading.Absent) {
            bytes.WriteByte(value: 0);
            digest = Sha256Digest.Compute(content: bytes.WrittenSpan);
            return (build ? new WorldRecipientImage(digest: digest, cells: [], observation: null, bytes: bytes.WrittenCount) : null);
        }

        var declaration = arena.Rows[row];
        var whole = (mask.Reading == RowReading.Whole);
        // A host-owned row has no observation, and a row's own policy decides for the rest.
        var observes = (!declaration.HostOwned && mask.ReadsRow);
        var pass = new WorldStateDisclosure.RowPass {
            Bytes = bytes,
            Exported = (build ? (export?.Cells ?? []) : null),
            Keys = keys,
            Observed = ((build && observes) ? [] : null),
            Replica = (build ? [] : null),
        };

        bytes.WriteByte(value: ((byte)(1 | (observes ? 2 : 0))));
        if (arena.StoresCells(rowOrdinal: row)) {
            WorldStateDisclosure.VisitRow(arena: arena, observes: observes, pass: ref pass, reader: ref mask, replicas: true, row: declaration, rowOrdinal: row, whole: whole);
        } else if (whole && (declaration.Cells is { Count: > 0 } declared)) {
            // A row the arena does not store carries its declared cells, which never change while the arena stands.
            bytes.WriteByte(value: DeclaredCells);
            bytes.WriteBytes(value: CanonicalJsonDocument.SerializeCompact(
                ((declaration as WorldStateRow) ?? new WorldStateRow(declaration, false, null)), WorldJsonContext.Default.WorldStateRow));
            for (var index = 0; (pass.Replica is { } cells) && (index < declared.Count); index++) {
                cells.Add(item: ((declared[index].Value.Kind == CellKind.Vector) ? (declared[index] with { Value = declared[index].Value.DetachedCopy() }) : declared[index]));
            }
        }
        bytes.WriteByte(value: 0);
        if (observes) {
            bytes.WriteCodeUnits(value: declaration.Name.Value);
            bytes.WriteByte(value: ((byte)declaration.Kind));
            bytes.WriteByte(value: (declaration.IsSlot ? ((byte)1) : ((byte)0)));
            bytes.WriteVarUInt(value: ((uint)pass.Hidden));
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
        digest = Sha256Digest.Compute(content: bytes.WrittenSpan);
        if (!build) { return null; }
        return new WorldRecipientImage(
            bytes: bytes.WrittenCount,
            cells: [.. pass.Replica!],
            digest: digest,
            observation: ((pass.Observed is { } observed) ? WorldStateDisclosure.ObservedRow(cells: Array.AsReadOnly(array: observed.ToArray()), hidden: pass.Hidden, row: declaration) : null)
        );
    }
    private static void WriteOptional(IBufferWriter<byte> bytes, bool present) => bytes.WriteByte(value: (present ? ((byte)1) : ((byte)0)));

    private static readonly Composition AbsentRow = ComposeAbsent();
    private static Composition ComposeAbsent() {
        var composition = new Composition(reading: RowReading.Absent, mask: [], maskHash: 0UL) { Composed = true };

        composition.Image = Pass(arena: null!, build: true, bytes: new ArrayBufferWriter<byte>(), digest: out var digest, export: null, keys: null, mask: RowMask.Absent, row: -1);
        composition.Digest = digest;
        return composition;
    }

    /// <summary>Gets the image a view is disclosed of a row, building it from the store when the row's composition
    /// holds none: for a delivery that carries the row. A caller that only compares takes the digest
    /// (<see cref="RowDigest"/>), which builds nothing.</summary>
    internal WorldRecipientImage Image(WorldRecipientView view, int row) => Image(Audience(view), row);
    internal WorldRecipientImage Image(AudienceKey audience, int row) {
        var arena = audience.View.Arena;

        if (!Settled(arena)) { return Unsettled(audience.View, row, build: true, out _)!; }
        var composition = Reading(Fresh(audience), row);

        // An image the composition holds was built since the composition was last written, and the row has since
        // moved, if at all, only by writes this audience's key proof covers.
        if (composition.Image is { } held) { return held; }
        m_work.Count(RowImages);
        var image = Pass(arena: arena, build: true, bytes: m_imageBytes, digest: out var digest, export: Export(arena, row), keys: null, mask: composition.Mask, row: row)!;

        // The pass wrote the bytes its image describes. Were they not the bytes the composition's digest names, the
        // store would have moved under a version or a key proof that said it had not.
        if (digest != composition.Digest) { throw new InvalidOperationException("A row's store no longer holds what its composition's digest names."); }
        return composition.Image = image;
    }
    // The digest of what a view is disclosed of a row: no image is built, and a row whose content stands costs a
    // version compare.
    internal Sha256Digest RowDigest(AudienceKey audience, int row) {
        if (!Settled(audience.View.Arena)) {
            _ = Unsettled(audience.View, row, build: false, out var live);
            return live;
        }
        return Reading(Fresh(audience), row).Digest;
    }
    // A row read while a journal scope is open. Row versions settle when the outermost scope commits, so nothing the
    // table keeps (a mask, a composition, an image) can be told current from stale until then: the row is read as the
    // store stands, under a mask asked for now, and nothing of the reading is kept. A rewind then leaves the table as
    // the scope found it. StateArena.ComputeHash treats an open scope the same way.
    private WorldRecipientImage? Unsettled(WorldRecipientView view, int row, bool build, out Sha256Digest digest) {
        var arena = view.Arena;
        var mask = RowMask.Absent;

        if (view.Includes(row)) {
            var observer = view.Observer;

            mask = (observer.ReadsWhole(row) ? RowMask.Whole : RowMask.Take(arena: arena, observer: ref observer, row: row, scratch: ref m_maskScratch));
        }
        Compositions++;
        m_work.Count(RowCompositions);
        if (build) { m_work.Count(RowImages); }
        return Pass(arena: arena, build: build, bytes: m_imageBytes, digest: out digest, export: ((build && (mask.Reading != RowReading.Absent)) ? arena.ToRow(row) : null), keys: null, mask: mask, row: row);
    }

    // The one place an audience decides what it is disclosed of a row, outside an open scope. It answers three
    // questions of the view, each read live: whether the row reaches its ceiling at all, whether it reads the row
    // whole, and otherwise its mask (RowMask.Take). Everything after that is the store and the mask. An audience's
    // mask is kept while the row's version and its dependencies' versions stand, since every answer in it has one of
    // them as its witness: a cell policy and a member by the row's version, a containing zone's members and cell
    // policies and a readers row's tokens by the dependencies', and the declared policies' answers by the audience key
    // the entry is filed under.
    private Composition Reading(AudienceKey audience, int row) {
        var view = audience.View;

        if (!view.Includes(row)) { return AbsentRow; }
        var arena = view.Arena;
        var observer = view.Observer;
        var version = arena.RowVersion(row);

        if (observer.ReadsWhole(row)) { return Written(arena, row, (m_whole[row] ??= new Composition(reading: RowReading.Whole, mask: [], maskHash: 0UL)), version); }
        var dependencies = DependencyVersions(arena, row);
        var key = (row, audience.Text);

        if (m_rows.TryGetValue(key, out var entry) && (entry.Dependencies == dependencies)) {
            if (entry.Version == version) { return entry.Composition; }
            // A write that named no key the audience reads left its mask and its content as they were. Rows born after
            // the window opened have no captured version to prove that against.
            var keys = entry.Composition.Keys.AsSpan(0, entry.Composition.KeyCount);

            if (ReferenceEquals(arena, m_windowArena) && ((uint)row < (uint)m_windowVersions.Length) && (entry.Version >= m_windowVersions[row]) &&
                (m_changes is not null) && !m_changes.Overlaps(row, keys) && !arena.ChangedKeys.Overlaps(row, keys)) {
                Skipped++;
                m_work.Count(RowSkips);
                entry.Version = version;
                return entry.Composition;
            }
        }

        var composition = Written(arena, row, Masked(row, RowMask.Take(arena: arena, observer: ref observer, row: row, scratch: ref m_maskScratch).Bytes), version);

        if (entry is null) { m_rows[key] = entry = new Entry(); }
        entry.Version = version; entry.Dependencies = dependencies; entry.Composition = composition;
        return composition;
    }
    // The composition of one mask over a row, found by the mask's bytes: equal bytes, never an equal hash, share it. A
    // row's masks that no audience has answered with for a window are let go once the row holds more than a few; an
    // audience that still holds one keeps reading it, and a later audience with the same mask composes its own.
    private Composition Masked(int row, ReadOnlySpan<byte> mask) {
        var hash = Puck.Maths.Fnv1aHash.Create();

        hash.Add(values: mask);
        var held = (m_masked[row] ??= []);
        Composition? found = null;

        for (var index = (held.Count - 1); index >= 0; index--) {
            var candidate = held[index];

            if ((candidate.MaskHash == hash.Value) && candidate.Mask.Bytes.SequenceEqual(mask)) {
                found = candidate;
            } else if ((held.Count > MaskedRetained) && (candidate.Window < (m_window - 1))) {
                held.RemoveAt(index);
            }
        }
        if (found is null) { held.Add(found = new Composition(reading: RowReading.Restricted, mask: mask.ToArray(), maskHash: hash.Value)); }
        found.Window = m_window;
        return found;
    }
    private const int MaskedRetained = 8;

    // Brings a composition current to its row: one pass over the store and the mask, whose bytes are digested in the
    // table's reused buffer and never kept. Every such write drops the composition's image, whether or not the digest
    // moved, so an image is never carried from one state of the row to another on the strength of a digest: the next
    // delivery to carry the row builds it from the store. A stream whose digest stands keeps the image it holds itself
    // (WorldDeliveryCursor), which costs nothing.
    private Composition Written(StateArena arena, int row, Composition composition, ulong version) {
        if (composition.Composed && (composition.Version == version)) { return composition; }
        var keyed = (composition.Mask.Reading == RowReading.Restricted);

        _ = Pass(arena: arena, build: false, bytes: m_imageBytes, digest: out var digest, export: null, keys: (keyed ? m_keyScratch : null), mask: composition.Mask, row: row);
        Compositions++; ComposedBytes += m_imageBytes.WrittenCount;
        m_work.Count(RowCompositions); m_work.Add(RowBytes, m_imageBytes.WrittenCount);
        composition.Image = null;
        composition.Digest = digest;
        composition.Version = version;
        composition.Composed = true;
        ArrayCapacity.EnsureLength(ref composition.Keys, (keyed ? m_keyScratch.Count : 0));
        if (keyed) { m_keyScratch.CopyTo(composition.Keys); }
        composition.KeyCount = (keyed ? m_keyScratch.Count : 0);
        return composition;
    }
    // A row's live export, sharing every cell its previous export from the same arena still holds unchanged.
    private StateRow Export(StateArena arena, int row) {
        if (!ReferenceEquals(m_exportArena, arena) || (m_exports.Length != arena.Rows.Count)) {
            m_exportArena = arena;
            m_exports = new StateRow?[arena.Rows.Count];
        }
        return m_exports[row] = arena.ToRow(row, previous: m_exports[row]);
    }
    // The keys a row holds, in order, as one list every stream that reads the row whole shares: the same list while
    // the row's structure version stands, and after it moves for as long as the keys read the same. Inside an open
    // scope the keys are read as the store stands and nothing is kept.
    internal WorldRowMembers Members(WorldRecipientView view, int row) {
        Prepare(view);
        if (!view.Includes(row)) { return WorldRowMembers.Empty; }
        var arena = view.Arena;
        var settled = Settled(arena);
        var structure = arena.RowStructureVersion(row);

        if (settled && (m_members[row] is { } held) && (m_memberStructures[row] == structure)) { return held; }
        if (!arena.StoresCells(row)) {
            var declared = ((arena.Rows[row].Cells is { Count: > 0 } cells) ? new WorldRowMembers([.. cells.Select(cell => cell.Key)]) : WorldRowMembers.Empty);

            if (settled) { m_members[row] = declared; m_memberStructures[row] = structure; }
            return declared;
        }
        var kept = ((settled && (m_members[row] is { } before)) ? before.Keys : default);
        var cursor = 0;
        var count = 0;
        var same = true;

        while (arena.TryNextCell(row, ref cursor, out var key)) {
            same &= (count < kept.Length) && (kept[count] == arena.Keys[key]);
            count++;
        }
        if (settled) { m_memberStructures[row] = structure; }
        if (settled && same && (count == kept.Length) && (m_members[row] is { } standing)) { return standing; }
        var keys = new CellName[count];

        cursor = 0;
        for (var index = 0; (index < count) && arena.TryNextCell(row, ref cursor, out var key); index++) { keys[index] = arena.Keys[key]; }
        var members = new WorldRowMembers(keys);

        if (settled) { m_members[row] = members; }
        return members;
    }
}
