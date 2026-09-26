using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The moved set of one composed dynamic-transform table: which slot ranges each produced frame rewrote, kept for the
/// last <see cref="History"/> frames so every engine that consumes the table — the primary view, and each offscreen
/// view that renders only some frames — stages exactly the rows that changed since the frame it last consumed.
/// <para>
/// A frame is either incremental, owing only the ranges its emitters reported through <see cref="Commit"/>, or
/// owes everything (<see cref="Everything"/>): the first frame, a program rebuild, a park-position change. An engine
/// whose last consumed frame has fallen out of the history, or that has never consumed this table, is owed
/// everything too. There is no other refresh path: an engine never compares the whole table against its mirror.
/// </para>
/// <para>
/// Emitters pack an owner's slots (an avatar's leaf range, a stamp registration's root and shapes) only while it is
/// restless, and hand <see cref="Commit"/> the slots' prior contents: an owner whose repack changed nothing has come
/// to rest and owes nothing. A still frame therefore packs no rows, compares no bytes and owes nothing, and a frame
/// moving k owners does work proportional to k. The source counts that work as <c>sdf.transforms.*</c>.
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
        workClass: WorkClass.Deterministic
    );
    /// <summary>Counts the packed bytes a restless owner's repack compared against the slots' prior contents.</summary>
    public static readonly WorkKind ComparedBytes = new(
        name: "sdf.transforms.compared-bytes",
        unit: "bytes",
        workClass: WorkClass.Deterministic
    );
    /// <summary>Counts the rows a frame owed its consumers: the ranges whose repack changed them, or the whole table on
    /// a frame that owes everything.</summary>
    public static readonly WorkKind OwedRows = new(
        name: "sdf.transforms.owed-rows",
        unit: "rows",
        workClass: WorkClass.Deterministic
    );

    private static readonly WorkKind[] DeclaredKinds = [PackedRows, ComparedBytes, OwedRows];

    // The packed size of one slot in the engine's table (three float4 rows), which is what a compare stands for.
    private const int PackedSlotBytes = (sizeof(float) * 12);

    private readonly bool[] m_everything = new bool[History];
    private readonly GpuUploadRuns[] m_frames = new GpuUploadRuns[History];
    private readonly long[] m_serials = new long[History];

    private WorkCount m_comparedBytes;
    private WorkCount m_owedRows;
    private WorkCount m_packedRows;
    private long m_serial;
    private int m_slot;
    private int m_tableRows;

    /// <summary>Initializes a new instance of the <see cref="SdfMovedTransforms"/> class.</summary>
    public SdfMovedTransforms() {
        for (var index = 0; (index < History); index++) {
            m_frames[index] = new GpuUploadRuns(capacity: GpuRegion.MaxCopyRuns);
        }
    }

    /// <summary>Gets a value indicating whether the current frame owes every slot.</summary>
    public bool Everything => m_everything[m_slot];
    /// <summary>Gets the current frame's serial, starting at 1 for the first frame; 0 before any frame began.</summary>
    public long Serial => m_serial;
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
        ArgumentOutOfRangeException.ThrowIfNegative(value: tableRows);

        m_serial++;
        m_slot = ((int)(m_serial % History));
        m_everything[m_slot] = everything;
        m_frames[m_slot].Clear();
        m_serials[m_slot] = m_serial;
        m_tableRows = tableRows;

        if (everything) {
            m_owedRows.Add(amount: tableRows);
        }
    }
    /// <summary>Settles one owner's repacked slots: counts the rows packed and the bytes compared, and owes the range
    /// when any slot differs from its prior contents.</summary>
    /// <param name="slots">The whole shared table, already holding the owner's repacked slots.</param>
    /// <param name="start">The owner's first slot.</param>
    /// <param name="previous">The owner's slots as they stood before the repack, one per repacked slot.</param>
    /// <returns><see langword="true"/> when the repack moved any slot, so the owner stays restless.</returns>
    public bool Commit(ReadOnlySpan<DynamicTransform> slots, int start, ReadOnlySpan<DynamicTransform> previous) {
        var packed = slots.Slice(
            length: previous.Length,
            start: start
        );

        m_packedRows.Add(amount: packed.Length);
        m_comparedBytes.Add(amount: (((long)packed.Length) * PackedSlotBytes));

        var moved = false;

        for (var index = 0; (index < packed.Length); index++) {
            if (!packed[index].Equals(other: previous[index])) {
                moved = true;

                break;
            }
        }

        if (moved) {
            Owe(
                count: packed.Length,
                start: start
            );
        }

        return moved;
    }
    /// <summary>Owes a range the current frame rewrote without a compare — an owner that vacated its slots, or one
    /// a caller already knows moved.</summary>
    /// <param name="start">The range's first slot.</param>
    /// <param name="count">The range's length.</param>
    public void Owe(int start, int count) {
        if (
            (count <= 0) ||
            m_everything[m_slot]
        ) {
            return;
        }

        m_frames[m_slot].Add(
            length: count,
            start: start
        );
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

    // Unions every range the frames after `since` owed into `into`. False when the consumer is owed everything: one of
    // those frames owed everything, or `since` has fallen out of the history.
    internal bool TryCollect(long since, GpuUploadRuns into) {
        into.Clear();

        if (since >= m_serial) {
            return true;
        }

        if (
            (since <= 0L) ||
            ((m_serial - since) >= History)
        ) {
            return false;
        }

        for (var serial = (since + 1L); (serial <= m_serial); serial++) {
            var slot = ((int)(serial % History));

            if (m_everything[slot]) {
                return false;
            }

            var frame = m_frames[slot];

            for (var run = 0; (run < frame.Count); run++) {
                into.Add(
                    length: frame.Length(index: run),
                    start: frame.Start(index: run)
                );
            }
        }

        return true;
    }
}
