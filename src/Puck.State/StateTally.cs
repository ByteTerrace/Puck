using Puck.Abstractions;
using Puck.Maths;

namespace Puck.State;

/// <summary>What one probe walk of a <see cref="StateDomain.Tally"/> row found.</summary>
public enum TallyProbeCase : byte {
    /// <summary>The walk faulted or met a table no tally write could leave: nothing may be read or written.</summary>
    Inconsistent = 0,
    /// <summary>The key holds the bucket the walk ended at.</summary>
    Found,
    /// <summary>The key holds no bucket; the walk ended at the free bucket a new key claims.</summary>
    Free,
}
/// <summary>The answer of one probe walk over a <see cref="StateDomain.Tally"/> row.</summary>
/// <param name="Case">What the walk found.</param>
/// <param name="Bucket">The bucket the walk ended at: the key's own, or the free one it would claim.</param>
/// <param name="Count">The key's count when <see cref="TallyProbeCase.Found"/>; otherwise zero.</param>
/// <param name="Epoch">The row's current reset epoch: the stamp a bucket claimed since the last reset carries.</param>
/// <param name="Entries">How many distinct keys the row holds since its last reset.</param>
public readonly record struct TallyProbe(TallyProbeCase Case, int Bucket, long Count, long Epoch, long Entries);
/// <summary>The one home of a <see cref="StateDomain.Tally"/> row's storage and its probe walk, which every reader and
/// writer of a tally goes through.</summary>
/// <remarks>
/// <para>A tally's cells are keyed by their index (the catalog interns every index the row's
/// <see cref="CellCount"/> spans, so no write mints a runtime key name). Cell <see cref="EpochCell"/> holds the reset
/// epoch and cell <see cref="EntriesCell"/> the keys held since it; then each of <see cref="BucketCount"/> buckets
/// holds three cells: its key (<see cref="KeyCell"/>), its count (<see cref="CountCell"/>) and the epoch it was
/// claimed in (<see cref="StampCell"/>). A bucket whose stamp is not the current epoch is free, so a reset is one
/// epoch write however many keys the row held.</para>
/// <para>The table is open addressed with linear probing from a key's home bucket, its SplitMix64 mix
/// (<see cref="InvertibleBitMix.Mix64"/>) masked to the power-of-two bucket count. The bucket count is at least twice
/// the capacity and keys are never removed within an epoch, so at most <see cref="StateDomain.Tally.Capacity"/> buckets are claimed and a
/// walk passes at most that many before it meets the key or a free bucket: <see cref="Probes"/> bounds every walk,
/// which is what the work sheet prices. The invariant rests on this type being the only writer of a tally's cells
/// through the rule language; another door that writes a tally's raw cells can break it, and a walk then answers
/// <see cref="TallyProbeCase.Inconsistent"/> rather than run past its bound.</para>
/// </remarks>
public static class StateTally {
    /// <summary>The most distinct keys a tally holds: the largest capacity whose table (a power of two at least twice
    /// the capacity, three cells a bucket, two header cells) fits <see cref="StateCapacity.MaxCellsPerRow"/>.</summary>
    public const int MaxCapacity = 512;
    /// <summary>The cell holding the row's reset epoch.</summary>
    public const int EpochCell = 0;
    /// <summary>The cell holding how many distinct keys the row holds since its last reset.</summary>
    public const int EntriesCell = 1;

    private const int HeaderCells = 2;
    private const int CellsPerBucket = 3;

    /// <summary>Returns how many buckets a tally of a capacity probes over: the power of two at least twice the
    /// capacity.</summary>
    /// <param name="capacity">The tally's capacity, from 1 to <see cref="MaxCapacity"/>.</param>
    /// <returns>The bucket count.</returns>
    public static int BucketCount(int capacity) => ((int)((uint)Math.Max(val1: 2, val2: (2 * capacity))).NextPowerOfTwo());
    /// <summary>Returns how many cells a tally of a capacity spans.</summary>
    /// <param name="capacity">The tally's capacity, from 1 to <see cref="MaxCapacity"/>.</param>
    /// <returns>The cell count.</returns>
    public static int CellCount(int capacity) => (HeaderCells + (CellsPerBucket * BucketCount(capacity: capacity)));
    /// <summary>Returns the most buckets one probe walk visits: one past the capacity, since a walk passes only claimed
    /// buckets before it ends.</summary>
    /// <param name="capacity">The tally's capacity.</param>
    /// <returns>The walk bound.</returns>
    public static int Probes(int capacity) => (capacity + 1);
    /// <summary>Returns the cell holding a bucket's key.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <returns>The cell index.</returns>
    public static int KeyCell(int bucket) => (HeaderCells + (CellsPerBucket * bucket));
    /// <summary>Returns the cell holding a bucket's count.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <returns>The cell index.</returns>
    public static int CountCell(int bucket) => (KeyCell(bucket: bucket) + 1);
    /// <summary>Returns the cell holding the epoch a bucket was claimed in.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <returns>The cell index.</returns>
    public static int StampCell(int bucket) => (KeyCell(bucket: bucket) + 2);
    /// <summary>Returns the interned key of one tally cell, the address a write to it names.</summary>
    /// <param name="arena">The arena the row lives in.</param>
    /// <param name="cell">The cell index, below the row's <see cref="CellCount"/>.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static CellKey KeyOf(StateArena arena, int cell) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        return arena.Catalog.IndexKey(position: cell);
    }
    /// <summary>Returns whether a row is a well-formed tally: an Int row with a capacity from 1 to
    /// <see cref="MaxCapacity"/> on its domain, carrying no trait that would change what a cell write stores, and no
    /// cell outside its table.</summary>
    /// <param name="row">The row, which must declare a <see cref="StateDomain.Tally"/> domain.</param>
    /// <param name="reason">Why the row is refused, or empty.</param>
    /// <returns><see langword="true"/> when the row is a tally the engine can hold.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public static bool TryValidate(StateRow row, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (row.EffectiveDomain is not StateDomain.Tally tally) {
            reason = "the row does not declare a tally domain";
            return false;
        }
        if (row.Kind != CellKind.Int) {
            reason = "a tally counts in an Int row";
            return false;
        }
        if ((tally.Capacity < 1) || (tally.Capacity > MaxCapacity)) {
            reason = $"tally capacity {tally.Capacity} must be between 1 and {MaxCapacity}";
            return false;
        }
        if (row.Capacity is not null) {
            reason = "a tally's capacity lives on its domain, not the row's own 'capacity' member";
            return false;
        }
        if (
            (row.Min is not null) || (row.Max is not null) || row.Evicts || (row.Advance is not null) ||
            (row.Dynamics is not null) || (row.Cycle is not null) || row.IsDraw || (row.Phase is not null) ||
            (row.ValuesFrom is not null) || (row.Inverse is not null) || (row.Knowledge is not null) ||
            (row.Enum is not null) || row.HostOwned || row.CellVisibility
        ) {
            reason = "a tally admits no envelope, eviction, time, draw, phase, value-typing, knowledge, enum or per-cell visibility trait: its cells are the engine's table";
            return false;
        }

        var cells = CellCount(capacity: tally.Capacity);

        foreach (var cell in (row.Cells ?? [])) {
            if (!CanonicalDecimal.TryParse(text: cell.Key.Value, value: out var index) || (((ulong)index) >= ((ulong)cells))) {
                reason = $"cell '{cell.Key.Value}' is not one of the tally's {cells} table cells";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }
    /// <summary>Reads a tally's reset epoch and the keys it holds since that reset; an unwritten cell reads zero.</summary>
    /// <param name="arena">The arena the row lives in, read with any open journal scope's writes.</param>
    /// <param name="rowOrdinal">The tally row's catalog ordinal.</param>
    /// <param name="time">The clocks a read is evaluated against.</param>
    /// <param name="epoch">The reset epoch.</param>
    /// <param name="entries">The distinct keys held since the reset.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static void ReadHeader(StateArena arena, int rowOrdinal, in ArenaTime time, out long epoch, out long entries) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        epoch = (Read(arena: arena, cell: EpochCell, rowOrdinal: rowOrdinal, time: in time, value: out var held) ? held : 0L);
        entries = (Read(arena: arena, cell: EntriesCell, rowOrdinal: rowOrdinal, time: in time, value: out held) ? held : 0L);
    }
    /// <summary>Walks a tally from a key's home bucket to the bucket holding the key or the free one it would claim.</summary>
    /// <param name="arena">The arena the row lives in, read with any open journal scope's writes.</param>
    /// <param name="rowOrdinal">The tally row's catalog ordinal.</param>
    /// <param name="capacity">The row's tally capacity.</param>
    /// <param name="key">The key.</param>
    /// <param name="time">The clocks a read is evaluated against.</param>
    /// <returns>What the walk found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static TallyProbe Find(StateArena arena, int rowOrdinal, int capacity, long key, in ArenaTime time) {
        ReadHeader(arena: arena, entries: out var entries, epoch: out var epoch, rowOrdinal: rowOrdinal, time: in time);

        if ((entries < 0L) || (entries > capacity)) {
            return new TallyProbe(Bucket: -1, Case: TallyProbeCase.Inconsistent, Count: 0L, Entries: entries, Epoch: epoch);
        }

        var mask = (BucketCount(capacity: capacity) - 1);
        var home = ((int)(InvertibleBitMix.Mix64(value: unchecked((ulong)key)) & ((ulong)mask)));

        for (var probe = 0; (probe < Probes(capacity: capacity)); probe++) {
            var bucket = ((home + probe) & mask);

            if (!Read(arena: arena, cell: StampCell(bucket: bucket), rowOrdinal: rowOrdinal, time: in time, value: out var stamp) || (stamp != epoch)) {
                return new TallyProbe(Bucket: bucket, Case: TallyProbeCase.Free, Count: 0L, Entries: entries, Epoch: epoch);
            }
            if (Read(arena: arena, cell: KeyCell(bucket: bucket), rowOrdinal: rowOrdinal, time: in time, value: out var heldKey) && (heldKey == key)) {
                _ = Read(arena: arena, cell: CountCell(bucket: bucket), rowOrdinal: rowOrdinal, time: in time, value: out var count);
                return new TallyProbe(Bucket: bucket, Case: TallyProbeCase.Found, Count: count, Entries: entries, Epoch: epoch);
            }
        }

        return new TallyProbe(Bucket: -1, Case: TallyProbeCase.Inconsistent, Count: 0L, Entries: entries, Epoch: epoch);
    }

    private static bool Read(StateArena arena, int rowOrdinal, int cell, in ArenaTime time, out long value) =>
        arena.TryReadLiveNumber(key: arena.Catalog.IndexKey(position: cell), rowOrdinal: rowOrdinal, time: in time, value: out value);
}
