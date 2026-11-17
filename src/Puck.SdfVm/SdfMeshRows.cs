using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>A retained draw table. Only its creator receives the writer; frames expose this reader.</summary>
public sealed class SdfMeshRows {
    /// <summary>Starts over once free rows exceed one third of the extent.</summary>
    public const int PackingFreeDivisor = 3;

    /// <summary>An owner's allocation, protected against reuse by the allocator's ticket.</summary>
    public readonly record struct Run(int Start, int Count, ulong Ticket);
    /// <summary>The picking provenance of a row: a host ordinal, program instance, or copied row.</summary>
    public readonly record struct Owner(int Host = -1, int Instance = -1, int CopiedRow = -1) {
        /// <summary>Creates provenance that names no host or program surface.</summary>
        public Owner() : this(-1, -1, -1) { }
    }

    private struct Cell {
        public SdfMeshDraw Base;
        public SdfMeshDraw Draw;
        public ulong Occupancy;
        public Owner Owner;
        public long IdentityRevision;
    }

    private readonly GpuRangeAllocator m_ranges = new(((ulong)SdfMeshRegion.MaxDraws));
    private readonly Dictionary<int, Run> m_runs = [];
    private readonly GpuChangedRows m_changes = new(capacity: GpuRegion.MaxCopyRuns, history: SdfMovedTransforms.History, stableRows: true, trackReseats: false);
    private readonly GpuUploadRuns m_pending = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly HashSet<int> m_ownedChanges = [];
    private readonly SdfMeshClaims m_claims = new();
    private Cell[] m_cells = [];

    private ulong m_occupancy;
    private bool m_everything;

    private bool m_startingOver = true;

    private SdfMeshRows() { }

    /// <summary>Creates a reader and its sole writer. No reader can obtain that writer later.</summary>
    public static (SdfMeshRows Rows, Writer Writer) Create() => Writer.Create();

    /// <summary>Gets the highest allocated row's end, including interior holes.</summary>
    public int Count => ((int)m_ranges.LiveEnd);
    /// <summary>Gets the populated row count.</summary>
    public int LiveCount { get; private set; }
    /// <summary>Gets the live glass row count, maintained by writes.</summary>
    public int GlassCount { get; private set; }
    /// <summary>Gets the published content revision.</summary>
    public long Revision { get; private set; }
    /// <summary>Gets whether allocations still have the canonical rebuild order.</summary>
    public bool Canonical { get; private set; }
    /// <summary>Gets the journal's read side.</summary>
    public GpuRowChanges Changes => m_changes.Reader;
    /// <summary>Gets the published journal endpoint.</summary>
    public long Serial => m_changes.Serial;
    /// <summary>Gets why the journal entry ending at <see cref="Serial"/> owes every row: the dresser's statement at the
    /// publication that began that entry (<see cref="SdfMeshStagingCause.Rebuild"/>, <see cref="SdfMeshStagingCause.Portal"/> or
    /// <see cref="SdfMeshStagingCause.Retry"/>), or <see langword="null"/> when the entry names rows. The tables count the
    /// records a staging packs under it (<see cref="SdfMeshHistory.RowsPackedOf"/>).</summary>
    public SdfMeshStagingCause? Cause { get; private set; }

    /// <summary>Reads a staged row, or the absent draw for a free row.</summary>
    public SdfMeshDraw this[int row] => ((((uint)row) < ((uint)Count)) ? m_cells[row].Draw : default);

    /// <summary>Reads the unclipped draw supplied by its owner.</summary>
    public SdfMeshDraw BaseAt(int row) => m_cells[row].Base;
    /// <summary>Gets a row's allocation incarnation; zero means free.</summary>
    public ulong Occupancy(int row) => ((((uint)row) < ((uint)Count)) ? m_cells[row].Occupancy : 0);
    /// <summary>Gets a live row's picking provenance.</summary>
    public Owner OwnerOf(int row) => m_cells[row].Owner;
    /// <summary>Whether the row still has the identity rendered under a captured table revision.</summary>
    public bool IdentityStands(int row, long revision) => ((((uint)row) < ((uint)Count)) && (m_cells[row].Occupancy != 0) && (m_cells[row].IdentityRevision <= revision));
    /// <summary>Enumerates live rows with their addresses and counts this whole-table walk.</summary>
    public IEnumerable<(int Row, SdfMeshDraw Draw)> Live() {
        for (var row = 0; (row < Count); row++) {
            if (m_cells[row].Draw.Mesh is null) { continue; }
            SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.RowsWalkedWhole);
            yield return (row, m_cells[row].Draw);
        }
    }

    /// <summary>The table's sole write capability, returned only by creation.</summary>
    public sealed class Writer {
        private readonly SdfMeshRows m_table;

        private Writer(SdfMeshRows table) { m_table = table; }

        internal static (SdfMeshRows, Writer) Create() { var rows = new SdfMeshRows(); return (rows, new Writer(table: rows)); }

        /// <summary>Allocates an owner run with a fresh occupancy at every address.</summary>
        public Run Allocate(int count) {
            if (count == 0) { return default; }
            var start = ((int)m_table.m_ranges.Allocate(((ulong)count)));
            var run = new Run(start, count, m_table.m_ranges.TicketOf(start: ((ulong)start)));

            m_table.m_runs.Add(key: start, value: run);
            if (m_table.m_cells.Length < (start + count)) { Array.Resize(array: ref m_table.m_cells, newSize: Math.Max(val1: (start + count), val2: (2 * m_table.m_cells.Length))); }
            for (var row = start; (row < (start + count)); row++) {
                m_table.m_ownedChanges.Add(item: row);
                m_table.m_cells[row].Occupancy = checked(++m_table.m_occupancy);
                m_table.m_cells[row].IdentityRevision = checked((m_table.Revision + 1));
            }
            m_table.Canonical = false;
            m_table.m_pending.Add(length: count, start: start);
            return run;
        }
        /// <summary>Frees a complete allocation by ticket, releasing its claims inside the same door.</summary>
        public void Free(Run run) {
            if (run.Count == 0) { return; }
            Validate(run: run);
            _ = m_table.m_ranges.Free(start: ((ulong)run.Start), ticket: run.Ticket);
            _ = m_table.m_runs.Remove(key: run.Start);
            for (var row = run.Start; (row < (run.Start + run.Count)); row++) {
                ref var cell = ref m_table.m_cells[row];

                if (cell.Draw.Mesh is { } mesh) {
                    m_table.m_ownedChanges.Add(item: row);
                    m_table.m_claims.Release(mesh: mesh);
                    m_table.LiveCount--;
                    if (cell.Draw.Glass) { m_table.GlassCount--; }
                }
                cell = new Cell { IdentityRevision = checked((m_table.Revision + 1)) };
            }
            m_table.m_pending.Add(run.Start, run.Count);
            m_table.Canonical = false;
        }
        /// <summary>Sets an allocated row and journals every content or picking identity change.</summary>
        public void Set(Run run, int offset, in SdfMeshDraw draw, Owner owner) {
            SetCore(draw: draw, offset: offset, owner: owner, run: run, setBase: true);
        }
        /// <summary>Writes a portal-derived draw while retaining its owner's base draw.</summary>
        public void SetStaged(Run run, int offset, in SdfMeshDraw draw, Owner owner) => SetCore(draw: draw, offset: offset, owner: owner, run: run, setBase: false);

        private void SetCore(Run run, int offset, in SdfMeshDraw draw, Owner owner, bool setBase) {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, run.Count);
            Validate(run: run);
            var row = (run.Start + offset);
            ref var cell = ref m_table.m_cells[row];

            if (setBase) { cell.Base = draw; }
            var previous = cell.Draw;
            var identityChanged = ((previous.Mesh is null) || !Equals(objA: previous.Identity, objB: draw.Identity) || (cell.Owner != owner));

            if (identityChanged || !ReferenceEquals(objA: previous.Mesh, objB: draw.Mesh)) { m_table.m_ownedChanges.Add(item: row); }
            if (!identityChanged && previous.SameStagedContent(other: in draw)) { return; }
            if (previous.Mesh is null) { m_table.m_claims.Claim(mesh: draw.Mesh); m_table.LiveCount++; } else { m_table.m_claims.Move(from: previous.Mesh, to: draw.Mesh); }
            if (previous.Glass) { m_table.GlassCount--; }
            if (draw.Glass) { m_table.GlassCount++; }
            cell.Draw = draw;
            cell.Owner = owner;
            if (identityChanged) { cell.IdentityRevision = checked((m_table.Revision + 1)); }
            m_table.m_pending.Add(length: 1, start: row);
        }

        /// <summary>Releases every run, including one not yet remembered by a failed publication's owner.</summary>
        public void StartOver() {
            foreach (var run in m_table.m_runs.Values.ToArray()) { Free(run: run); }
            m_table.m_everything = true;
            m_table.m_startingOver = true;
        }
        /// <summary>Publishes writes as one captured journal endpoint.</summary>
        /// <param name="cause">Why the entry this publication begins owes every row, or <see langword="null"/>; kept
        /// only when the publication begins an entry, so a publication that owes nothing leaves the entry's cause standing.</param>
        public void Publish(SdfMeshStagingCause? cause = null) {
            var owned = 0;

            foreach (var row in m_table.m_ownedChanges) { if (row < m_table.Count) { owned++; } }
            SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.RowsOwnedChanged, owned);
            m_table.m_ownedChanges.Clear();
            if (m_table.m_everything || (m_table.m_pending.Count > 0)) { m_table.Revision = checked((m_table.Revision + 1)); }
            // A publication that owes nothing and leaves the extent alone is the endpoint already published, so a consumer
            // current through it is still current and a frame that carries it stands.
            if (m_table.m_everything || (m_table.m_pending.Count > 0) || (m_table.m_changes.Serial == 0L) || (m_table.m_changes.RowCount != m_table.Count)) {
                m_table.m_changes.Begin(rows: m_table.Count, everything: m_table.m_everything);
                m_table.Cause = cause;
            }
            for (var run = 0; (run < m_table.m_pending.Count); run++) {
                var start = m_table.m_pending.Start(index: run);
                var length = Math.Min(val1: (m_table.Count - start), val2: m_table.m_pending.Length(index: run));

                if (length > 0) { m_table.m_changes.Owe(start, length); }
            }
            m_table.m_everything = false;
            m_table.m_pending.Clear();
            if (m_table.m_startingOver) { m_table.Canonical = true; m_table.m_startingOver = false; }
        }

        private void Validate(Run run) {
            if (!m_table.m_runs.TryGetValue(key: run.Start, value: out var allocated) || (allocated != run)) { throw new InvalidOperationException(message: "Stale or altered row run."); }
        }
    }
}

// A non-owning view over host inputs or retained rows. It cannot be downcast to a writable list.
// The one door a consumer of the frame's staged draws reads their revision and journal through: the dresser's retained rows
// when the frame was dressed, the producer's own list otherwise. A dressed frame's host inputs (MeshDraws and its journal)
// stay the dresser's inputs, so dressing the frame again reads them unchanged.
internal readonly struct SdfMeshDraws(SdfFrame frame) {
    public SdfMeshRows? Rows => frame.MeshRows;
    public long Revision => ((frame.MeshRows is { } rows) ? rows.Revision : frame.MeshDrawsRevision);
    public GpuRowChanges? Changes => ((frame.MeshRows is { } rows) ? rows.Changes : frame.MeshDrawChanges);
    public long Serial => ((frame.MeshRows is { } rows) ? rows.Serial : frame.MeshDrawChangesSerial);
    public SdfMeshStagingCause? Cause => frame.MeshRows?.Cause;
    public object Roster => (((object?)frame.MeshRows) ?? frame.MeshDraws);
    public int Count => (frame.MeshRows?.Count ?? frame.MeshDraws.Count);

    public SdfMeshDraw this[int row] => ((frame.MeshRows is { } rows) ? rows[row] : frame.MeshDraws[row]);

    public ulong Occupancy(int row) => (frame.MeshRows?.Occupancy(row: row) ?? 0);
}
