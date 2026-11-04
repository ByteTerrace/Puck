namespace Puck.Abstractions.Gpu;

/// <summary>A bounded journal of changed table rows. Each consumer collects the frames after its own cursor through
/// an explicitly captured serial; a missing, expired or full frame requires a complete refresh. Runs may conservatively
/// include unchanged gaps when their fixed capacity coarsens them. The producer and consumers use one owning thread.</summary>
public sealed class GpuChangedRows {
    private readonly bool[] m_everything;
    private readonly GpuUploadRuns?[] m_frames;
    private readonly GpuUploadRuns?[]? m_reseats;
    private readonly long[] m_serials;
    private readonly int m_capacity;
    private int m_slot;

    /// <summary>Initializes a bounded row journal. Run storage is allocated only when a frame first owes rows, then
    /// reused when that frame's ring slot comes around again.</summary>
    /// <param name="history">The number of retained frame slots.</param>
    /// <param name="capacity">The maximum changed runs per frame, before conservative coarsening.</param>
    /// <param name="trackReseats">Whether to retain a separate stream of rows whose interpretation changed.</param>
    public GpuChangedRows(int history = 64, int capacity = GpuRegion.MaxCopyRuns, bool trackReseats = false) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: history, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: capacity, other: 2);
        m_capacity = capacity;
        m_everything = new bool[history];
        m_frames = new GpuUploadRuns?[history];
        m_reseats = (trackReseats ? new GpuUploadRuns?[history] : null);
        m_serials = new long[history];
    }

    /// <summary>Gets the latest produced serial, starting at one; zero before the first frame.</summary>
    public long Serial { get; private set; }
    /// <summary>Gets the latest frame's table row count.</summary>
    public int RowCount { get; private set; }
    /// <summary>Gets whether the latest frame owes the entire table.</summary>
    public bool Everything => m_everything[m_slot];
    /// <summary>Gets logical retained array payload bytes, including allocated run storage but excluding object and
    /// array headers. This is storage attribution, not a physical allocation claim.</summary>
    public long StorageBytes {
        get {
            var bytes = (((long)m_everything.Length * sizeof(bool)) + ((long)m_serials.Length * sizeof(long)) +
                ((long)m_frames.Length * IntPtr.Size) + ((long)(m_reseats?.Length ?? 0) * IntPtr.Size));
            for (var index = 0; index < m_frames.Length; index++) {
                bytes += (m_frames[index]?.StorageBytes ?? 0L);
                bytes += (m_reseats?[index]?.StorageBytes ?? 0L);
            }
            return bytes;
        }
    }

    /// <summary>Starts the next frame, clearing that ring slot's previous runs without freeing their storage.</summary>
    /// <param name="everything">Whether consumers must refresh every row for this frame.</param>
    /// <param name="rows">The table's current row count.</param>
    public void Begin(bool everything, int rows) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: rows);
        Serial = checked(Serial + 1L);
        m_slot = ((int)(Serial % m_frames.Length));
        m_everything[m_slot] = everything;
        m_frames[m_slot]?.Clear();
        m_reseats?[m_slot]?.Clear();
        m_serials[m_slot] = Serial;
        RowCount = rows;
    }

    /// <summary>Owes rows of the current frame, optionally reseating their previous interpretation. A frame already
    /// owing everything needs no run storage.</summary>
    /// <param name="start">The first row.</param>
    /// <param name="count">The number of rows; zero or less records nothing.</param>
    /// <param name="reseat">Whether these rows also start a new interpretation.</param>
    /// <exception cref="InvalidOperationException">No frame began, or reseats were requested without tracking them.</exception>
    public void Owe(int start, int count, bool reseat = false) {
        if (count <= 0) { return; }
        if (Serial == 0) { throw new InvalidOperationException(message: "Begin a frame before owing its rows."); }
        if (reseat && (m_reseats is null)) { throw new InvalidOperationException(message: "This row journal does not track reseats."); }
        ArgumentOutOfRangeException.ThrowIfNegative(value: start);
        if (count > (RowCount - start)) { throw new ArgumentOutOfRangeException(paramName: nameof(count), message: "An owed range must lie inside the current table."); }
        if (Everything) { return; }

        (m_frames[m_slot] ??= new GpuUploadRuns(capacity: m_capacity)).Add(start: start, length: count);
        if (reseat) { (m_reseats![m_slot] ??= new GpuUploadRuns(capacity: m_capacity)).Add(start: start, length: count); }
    }

    /// <summary>Collects the conservative union of rows changed in <c>(since, through]</c>. Clears the supplied changed output,
    /// appends reseats without clearing them, and changes neither consumer cursor. On failure the supplied changed output is
    /// empty and reseats are untouched: the consumer must refresh the complete table.</summary>
    /// <param name="since">The serial the consumer last consumed.</param>
    /// <param name="through">The serial captured with the table being consumed.</param>
    /// <param name="into">The changed-row output, cleared on entry, or null when only reseats are needed.</param>
    /// <param name="reseated">Optional reseat output, appended on success.</param>
    /// <returns>Whether the entire requested window is retained and incremental.</returns>
    public bool TryCollect(long since, long through, GpuUploadRuns? into, GpuUploadRuns? reseated = null) {
        into?.Clear();
        if ((since < 0L) || (through < since) || (through > Serial)) { return false; }
        if (Serial == 0L) { return ((since == 0L) && (through == 0L)); }
        if ((since <= 0L) || ((Serial - since) >= m_frames.Length)) { return false; }
        if (m_serials[((int)(through % m_frames.Length))] != through) { return false; }

        // Validate first: a later full or missing frame must not append partial reseats to the caller's live set.
        for (var serial = since; (serial < through);) {
            serial++;
            var slot = ((int)(serial % m_frames.Length));
            if ((m_serials[slot] != serial) || m_everything[slot]) { return false; }
        }
        for (var serial = since; (serial < through);) {
            serial++;
            var slot = ((int)(serial % m_frames.Length));
            if (into is not null) { Append(m_frames[slot], into); }
            if (reseated is not null) { Append(m_reseats?[slot], reseated); }
        }
        return true;
    }

    private static void Append(GpuUploadRuns? source, GpuUploadRuns destination) {
        if (source is null) { return; }
        for (var run = 0; run < source.Count; run++) { destination.Add(start: source.Start(index: run), length: source.Length(index: run)); }
    }
}
