using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The moved set of one composed dynamic-transform table: which slot ranges each produced frame rewrote, kept for the
/// last <see cref="History"/> frames so every engine that consumes the table — the primary view, and each offscreen
/// view that renders only some frames — stages exactly the rows that changed since the frame it last consumed.
/// <para>
/// A frame is either incremental, owing only the ranges its emitters reported through <see cref="Commit(int, int, bool, bool)"/>, owes
/// everything (the first frame, a park-position change), or repacks the whole table and owes the rows the repack
/// changed (a program rebuild, <see cref="BeginRepack"/>); emitters pack every owner in the last two
/// (<see cref="Everything"/>). An engine
/// whose last consumed frame has fallen out of the history, or that has never consumed this table, is owed
/// everything too. There is no other refresh path: an engine never compares the whole table against its mirror.
/// </para>
/// <para>
/// Emitters pack an owner's slots (an avatar's leaf range, a stamp registration's root and shapes) only while it is
/// restless, and either hand <see cref="Commit(ReadOnlySpan{DynamicTransform}, int, ReadOnlySpan{DynamicTransform}, bool)"/>
/// the slots' prior contents or write each slot through <see cref="Write"/> and settle with what it reported
/// (<see cref="Commit(int, int, bool, bool)"/>): an owner whose repack changed nothing and whose
/// history holds has come to rest and owes nothing. A still frame packs no rows, compares no bytes and owes nothing; a frame
/// moving k owners does work proportional to k. The source counts that work as <c>sdf.transforms.*</c>. These totals
/// have <see cref="WorkClass.Pacing"/> because presentation may interpolate several produced frames per simulation
/// tick, and each frame packs the pose it actually presents.
/// </para>
/// <para>
/// A range whose slots changed owner or cut their pose is reseated (<see cref="Commit(int, int, bool, bool)"/>'s <c>reseat</c>), even if
/// their new poses equal the old ones: a consumer seeds that range's previous transforms and dependent mesh history,
/// so a new owner never reads its predecessor's pose as motion. A consumer owed everything seeds every previous row.
/// </para>
/// </summary>
public sealed class SdfMovedTransforms : IWorkCounterSource {
    /// <summary>The number of recent frames whose moved ranges are kept for a consumer that skipped frames.</summary>
    public const int History = 64;
    /// <summary>The stable counter-source name.</summary>
    public const string SourceName = "sdf.transforms";

    /// <summary>Counts every dynamic-transform row an emitter packed, restless owners only.</summary>
    public static readonly WorkKind PackedRows = new(
        name: "sdf.transforms.packed-rows",
        unit: "rows",
        workClass: WorkClass.Pacing
    );
    /// <summary>Counts the packed bytes a restless owner's repack compared against the slots' prior contents.</summary>
    public static readonly WorkKind ComparedBytes = new(
        name: "sdf.transforms.compared-bytes",
        unit: "bytes",
        workClass: WorkClass.Pacing
    );
    /// <summary>Counts the rows a frame owed its consumers: the ranges whose repack changed them, or the whole table on
    /// a frame that owes everything.</summary>
    public static readonly WorkKind OwedRows = new(
        name: "sdf.transforms.owed-rows",
        unit: "rows",
        workClass: WorkClass.Pacing
    );

    private static readonly WorkKind[] DeclaredKinds = [PackedRows, ComparedBytes, OwedRows];

    // The packed size of one slot in the engine's table (three float4 rows), which is what a compare stands for.
    private const int PackedSlotBytes = (sizeof(float) * 12);

    private readonly GpuChangedRows m_changes = new(history: History, trackReseats: true);

    private WorkCount m_comparedBytes;
    private WorkCount m_owedRows;
    private WorkCount m_packedRows;
    private bool m_repacking;

    /// <summary>Gets a value indicating whether the current frame packs every slot: it owes every slot, or it repacks the
    /// whole table and owes only the rows that repack changed (<see cref="BeginRepack"/>). Either way the host has parked
    /// the table and every emitter packs every owner.</summary>
    public bool Everything => (m_changes.Everything || m_repacking);
    /// <summary>Gets the current frame's serial, starting at 1 for the first frame; 0 before any frame began.</summary>
    public long Serial => m_changes.Serial;
    /// <summary>Gets the logical retained array payload of the row journal, excluding object and array headers.</summary>
    public long StorageBytes => m_changes.StorageBytes;
    /// <inheritdoc/>
    public string Name => SourceName;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => DeclaredKinds;
    /// <summary>Gets the kinds every instance counts, in report order, for a reader that registers the source before
    /// an instance exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Starts the next frame's moved set.</summary>
    /// <param name="everything">Whether the frame owes every slot of the table.</param>
    /// <param name="tableRows">The table's length in slots.</param>
    public void Begin(bool everything, int tableRows) {
        m_changes.Begin(everything: everything, rows: tableRows);
        m_repacking = false;

        if (everything) {
            m_owedRows.Add(amount: tableRows);
        }
    }
    /// <summary>Starts the next frame as a repack of the whole table, as after a program rebuild that may give a slot
    /// another owner: every emitter packs every owner (<see cref="Everything"/>), and the frame owes no slot until
    /// <see cref="CompleteRepack"/> compares the repacked table with the one before it.</summary>
    /// <param name="tableRows">The table's length in slots.</param>
    public void BeginRepack(int tableRows) {
        Begin(everything: false, tableRows: tableRows);
        m_repacking = true;
    }
    /// <summary>Completes a repack (<see cref="BeginRepack"/>): owes, and reseats, each run of rows whose bytes differ
    /// from the table before the repack, and every row past the previous table's end, so a consumer stages the rows the
    /// rebuild moved and seeds their previous poses, as it does for a new owner, rather than every row.</summary>
    /// <param name="previous">The table as it stood before the repack.</param>
    /// <param name="current">The repacked table.</param>
    /// <exception cref="InvalidOperationException">The current frame is not a repack.</exception>
    public void CompleteRepack(ReadOnlySpan<DynamicTransform> previous, ReadOnlySpan<DynamicTransform> current) {
        if (!m_repacking) {
            throw new InvalidOperationException(message: "Only a frame begun as a repack completes one.");
        }

        m_repacking = false;

        var rows = Math.Min(
            val1: previous.Length,
            val2: current.Length
        );
        var size = Unsafe.SizeOf<DynamicTransform>();
        var before = MemoryMarshal.AsBytes(span: previous[..rows]);
        var after = MemoryMarshal.AsBytes(span: current[..rows]);
        var index = 0;

        while (index < after.Length) {
            index += after[index..].CommonPrefixLength(other: before[index..]);

            if (index >= after.Length) {
                break;
            }

            var row = (index / size);
            var end = (row + 1);

            while ((end < rows) && !after.Slice(length: size, start: (end * size)).SequenceEqual(other: before.Slice(length: size, start: (end * size)))) {
                end++;
            }

            Reseat(count: (end - row), start: row);
            index = (end * size);
        }

        Reseat(count: (current.Length - rows), start: rows);
    }
    /// <summary>Settles one owner's repacked slots: counts the rows packed and the bytes compared, and owes the range
    /// when any slot differs or a history cut is requested, including cuts with unchanged poses.</summary>
    /// <param name="slots">The whole shared table, already holding the owner's repacked slots.</param>
    /// <param name="start">The owner's first slot.</param>
    /// <param name="previous">The owner's slots as they stood before the repack, one per repacked slot.</param>
    /// <param name="reseat">Whether the slots hold another owner than they held when last packed (a spawn into a vacated
    /// range, a body index reused, a pose that jumped): the range is reseated even when its pose is unchanged, so each
    /// consumer seeds its previous poses rather than reading the departed pose as motion.</param>
    /// <returns><see langword="true"/> when the repack moved any slot, so the owner stays restless.</returns>
    /// <remarks>An owner whose pack writes its slots through <see cref="Write"/> knows whether it moved one and settles
    /// with <see cref="Commit(int, int, bool, bool)"/> instead, keeping no copy of the slots it rewrites.</remarks>
    public bool Commit(ReadOnlySpan<DynamicTransform> slots, int start, ReadOnlySpan<DynamicTransform> previous, bool reseat) {
        var packed = slots.Slice(
            length: previous.Length,
            start: start
        );

        m_comparedBytes.Add(amount: (((long)packed.Length) * PackedSlotBytes));

        var moved = false;

        for (var index = 0; (index < packed.Length); index++) {
            if (!packed[index].Equals(other: previous[index])) {
                moved = true;

                break;
            }
        }

        return Commit(count: packed.Length, moved: moved, reseat: reseat, start: start);
    }
    /// <summary>Settles one owner's repacked slots whose pack already knows whether it moved one, having written each
    /// through <see cref="Write"/>: counts the rows packed, and owes the range when a slot moved or a history cut is
    /// requested, including cuts with unchanged poses.</summary>
    /// <param name="start">The owner's first slot.</param>
    /// <param name="count">The owner's slot count.</param>
    /// <param name="moved">Whether the repack changed any of the owner's slots.</param>
    /// <param name="reseat">Whether the slots hold another owner than they held when last packed (see
    /// <see cref="Commit(ReadOnlySpan{DynamicTransform}, int, ReadOnlySpan{DynamicTransform}, bool)"/>).</param>
    /// <returns><paramref name="moved"/>, so the owner stays restless while it moves.</returns>
    public bool Commit(int start, int count, bool moved, bool reseat) {
        m_packedRows.Add(amount: count);

        // A cut changes ownership/history even when the new owner has exactly the departed pose. Keep the return
        // value about actual movement so an unchanged owner can settle without dropping that cut from the journal.
        if (reseat) {
            Reseat(count: count, start: start);
        } else if (moved) {
            Owe(count: count, start: start);
        }

        return moved;
    }
    /// <summary>Writes one slot of an owner's repack, comparing as it writes, so the pack keeps no copy of the slots it
    /// rewrites to learn whether it moved one.</summary>
    /// <param name="table">The whole shared table.</param>
    /// <param name="slot">The slot to write.</param>
    /// <param name="value">The slot's new transform.</param>
    /// <returns><see langword="true"/> when the slot held another transform.</returns>
    public static bool Write(Span<DynamicTransform> table, int slot, in DynamicTransform value) {
        ref var held = ref table[slot];

        if (held.Equals(other: value)) {
            return false;
        }

        held = value;

        return true;
    }
    /// <summary>Owes a range the current frame rewrote without a compare — an owner that vacated its slots, or one
    /// a caller already knows moved.</summary>
    /// <param name="start">The range's first slot.</param>
    /// <param name="count">The range's length.</param>
    public void Owe(int start, int count) {
        if (
            (count <= 0) ||
            Everything
        ) {
            return;
        }

        m_changes.Owe(count: count, start: start);
        m_owedRows.Add(amount: count);
    }
    // A repack's byte diff cannot discover equal-pose ownership cuts, so explicit reseats remain in its journal.
    // Only a genuinely full frame can omit them: its consumers already seed every row.
    private void Reseat(int start, int count) {
        if (
            (count <= 0) ||
            m_changes.Everything
        ) {
            return;
        }

        m_changes.Owe(count: count, start: start, reseat: true);
        m_owedRows.Add(amount: count);
    }
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        if (ReferenceEquals(
            objA: kind,
            objB: PackedRows
        )) {
            value = m_packedRows.Value;

            return true;
        }

        if (ReferenceEquals(
            objA: kind,
            objB: ComparedBytes
        )) {
            value = m_comparedBytes.Value;

            return true;
        }

        if (ReferenceEquals(
            objA: kind,
            objB: OwedRows
        )) {
            value = m_owedRows.Value;

            return true;
        }

        value = 0L;

        return false;
    }

    // Captured frames collect through their own serial, never through a later producer frame.
    internal bool TryCollect(long since, long through, GpuUploadRuns? into, GpuUploadRuns? reseated = null) =>
        m_changes.TryCollect(since: since, through: through, into: into, reseated: reseated);
}
