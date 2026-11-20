using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

/// <summary>A retained draw table. Only its creator receives the writer; frames expose this reader.</summary>
public sealed class SdfMeshRows {
    /// <summary>Starts over once free rows exceed one third of the extent.</summary>
    public const int PackingFreeDivisor = 3;

    /// <summary>An owner's allocation, protected against reuse by the allocator's ticket.</summary>
    public readonly record struct Run(int Start, int Count, ulong Ticket);
    /// <summary>The picking provenance of a row: a host ordinal, program instance, or copied row.</summary>
    public readonly record struct Owner(int Host = -1, int Instance = -1, int CopiedRow = -1, bool RigidCopy = false) {
        /// <summary>Creates provenance that names no host or program surface.</summary>
        public Owner() : this(-1, -1, -1) { }
    }

    private struct Cell {
        public SdfMeshDraw Base;
        public SdfMeshDraw Draw;
        public ulong Occupancy;
        public Owner Owner;
        public long IdentityRevision;
        public ClipState? Clip;
        public int CopyOffset;
    }

    private sealed class ClipState(SdfSlotClip clip) {
        public SdfSlotClip Clip = clip;
        public Run Copies;
        public readonly List<(int Row, ulong Occupancy)> Sources = [];
        public bool MembershipChanged;
    }

    private readonly GpuRangeAllocator m_ranges = new(((ulong)SdfMeshRegion.MaxDraws));
    private readonly Dictionary<int, Run> m_runs = [];
    private readonly GpuChangedRows m_changes = new(capacity: GpuRegion.MaxCopyRuns, history: SdfMovedTransforms.History, stableRows: true, trackReseats: false);
    private readonly GpuUploadRuns m_pending = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly HashSet<int> m_ownedChanges = [];
    private readonly SdfMeshClaims m_claims = new();
    private readonly GpuRowIndex<int> m_rowsBySlot = new();
    private readonly GpuRowIndex<ClipState> m_rowsByClip = new();
    private readonly List<ClipState> m_clips = [];
    private readonly HashSet<int> m_clipCandidates = [];
    private readonly List<(int Row, ulong Occupancy)> m_copySources = [];
    private Cell[] m_cells = [];

    private ulong m_occupancy;
    private bool m_everything;
    private bool m_portalChanged;

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
    /// <summary>Gets the retained clip indexes' logical row storage.</summary>
    public long ClipIndexBytes => (m_rowsBySlot.StorageBytes + m_rowsByClip.StorageBytes);
    /// <summary>Gets the cause of the journal entry ending at <see cref="Serial"/>: a start over's
    /// <see cref="SdfMeshStagingCause.Rebuild"/> or <see cref="SdfMeshStagingCause.Retry"/>, a clip derivation's
    /// <see cref="SdfMeshStagingCause.Portal"/>, or <see langword="null"/> for ordinary row changes. The tables count the
    /// records a staging packs under it (<see cref="SdfMeshHistory.RowsPackedOf"/>).</summary>
    public SdfMeshStagingCause? Cause { get; private set; }

    /// <summary>Reads a staged row, or the absent draw for a free row.</summary>
    public SdfMeshDraw this[int row] => ((((uint)row) < ((uint)Count)) ? m_cells[row].Draw : default);

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
            ArrayCapacity.EnsureLength(ref m_table.m_cells, start + count);
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

                if (cell.Clip is { } clip) { clip.MembershipChanged = true; m_table.m_portalChanged = true; }
                m_table.m_rowsByClip.Remove(row: row);
                m_table.m_rowsBySlot.Remove(row: row);
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
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, run.Count);
            Validate(run: run);
            var row = (run.Start + offset);

            m_table.m_cells[row].Base = draw;
            if (draw.FrameSlot is { } slot) { m_table.m_rowsBySlot.Set(row: row, key: slot); } else { m_table.m_rowsBySlot.Remove(row: row); }
            StageBase(row: row, owner: owner);
        }

        /// <summary>Derives clips in list order over the affected slots only, and reconciles each clip's carried run.</summary>
        public void SetClips(IReadOnlyList<SdfSlotClip> clips) {
            ArgumentNullException.ThrowIfNull(clips);
            var held = m_table.m_clips;

            m_table.m_clipCandidates.Clear();
            for (var i = 0; (i < Math.Max(held.Count, clips.Count)); i++) {
                if ((i < held.Count) && (i < clips.Count) && held[i].Clip.SameStagedContent(other: clips[i])) { continue; }
                if (i < held.Count) { AddClipCandidates(clip: held[i].Clip); }
                if (i < clips.Count) { AddClipCandidates(clip: clips[i]); }
            }
            // Update the complete ordered list before asking which clip wins any affected row.
            while (held.Count > clips.Count) {
                Free(run: held[^1].Copies);
                held.RemoveAt(index: (held.Count - 1));
            }
            for (var i = 0; (i < clips.Count); i++) {
                if (i == held.Count) { held.Add(item: new ClipState(clip: clips[i])); } else { held[i].Clip = clips[i]; }
            }
            foreach (var row in m_table.m_clipCandidates) { StageBase(row: row, owner: m_table.m_cells[row].Owner); }
            FlushCopies();
        }

        private void AddClipCandidates(SdfSlotClip clip) {
            for (long slot = clip.FirstSlot; ((slot <= int.MaxValue) && (slot < ((long)clip.FirstSlot + clip.SlotCount))); slot++) {
                for (var row = m_table.m_rowsBySlot.First(key: ((int)slot)); (row >= 0); row = m_table.m_rowsBySlot.Next(row: row)) { m_table.m_clipCandidates.Add(item: row); }
            }
        }

        private void StageBase(int row, Owner owner) {
            var draw = m_table.m_cells[row].Base;
            ClipState? selected = null;

            if (draw.FrameSlot is { } slot) {
                foreach (var clip in m_table.m_clips) { if (clip.Clip.Contains(slot: slot)) { selected = clip; break; } }
            }
            var previous = m_table.m_cells[row].Clip;

            if ((previous is not null) || (selected is not null)) { m_table.m_portalChanged = true; }
            if (!ReferenceEquals(objA: previous, objB: selected)) {
                if (previous is not null) { previous.MembershipChanged = true; }
                m_table.m_rowsByClip.Remove(row: row);
                if (selected is not null) { selected.MembershipChanged = true; m_table.m_rowsByClip.Set(row: row, key: selected); }
                m_table.m_cells[row].Clip = selected;
            }
            WriteDraw(row: row, draw: ((selected is null) ? draw : draw with { ClipPlane = selected.Clip.Plane }), owner: owner);
            if (selected is not null) {
                SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.RowsClippedOf(owner: owner));
                if (!selected.MembershipChanged) { StageCopy(source: row, clip: selected); }
            } else if (previous is not null) { SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.RowsRestoredOf(owner: owner)); }
        }

        private void StageCopy(int source, ClipState clip) {
            var row = (clip.Copies.Start + m_table.m_cells[source].CopyOffset);

            WriteDraw(row: row, draw: clip.Clip.Carried(draw: m_table.m_cells[source].Base), owner: new Owner(CopiedRow: source));
        }

        private void FlushCopies() {
            foreach (var clip in m_table.m_clips) {
                if (!clip.MembershipChanged) { continue; }
                var sources = m_table.m_copySources;

                sources.Clear();
                for (var row = m_table.m_rowsByClip.First(key: clip); (row >= 0); row = m_table.m_rowsByClip.Next(row: row)) { sources.Add(item: (row, m_table.m_cells[row].Occupancy)); }
                sources.Sort(comparison: static (a, b) => a.Row.CompareTo(value: b.Row));
                if (!sources.SequenceEqual(second: clip.Sources)) {
                    var old = clip.Copies;

                    clip.Copies = Allocate(count: sources.Count);
                    Free(run: old);
                    clip.Sources.Clear(); clip.Sources.AddRange(collection: sources);
                }
                for (var offset = 0; (offset < sources.Count); offset++) {
                    var source = sources[offset].Row;

                    m_table.m_cells[source].CopyOffset = offset;
                    StageCopy(source: source, clip: clip);
                }
                clip.MembershipChanged = false;
            }
        }

        private void WriteDraw(int row, in SdfMeshDraw draw, Owner owner) {
            ref var cell = ref m_table.m_cells[row];

            var previous = cell.Draw;
            var identityChanged = ((previous.Mesh is null) || !Equals(objA: previous.Identity, objB: draw.Identity) || (cell.Owner != owner));

            if (identityChanged || !ReferenceEquals(objA: previous.Mesh, objB: draw.Mesh)) { m_table.m_ownedChanges.Add(item: row); }
            if (!identityChanged && previous.SameStagedContent(other: in draw)) { return; }
            SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.RowsWrittenOf(owner: owner));
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
            m_table.m_clips.Clear();
            m_table.m_rowsByClip.Clear();
            m_table.m_rowsBySlot.Clear();
            m_table.m_clipCandidates.Clear();
            m_table.m_copySources.Clear();
            m_table.m_everything = true;
            m_table.m_startingOver = true;
        }
        /// <summary>Publishes writes as one captured journal endpoint.</summary>
        /// <param name="cause">An explicit publication cause, or <see langword="null"/> to derive portal or ordinary row changes; kept
        /// only when the publication begins an entry, so a publication that owes nothing leaves the entry's cause standing.</param>
        public void Publish(SdfMeshStagingCause? cause = null) {
            FlushCopies();
            var owned = 0;

            foreach (var row in m_table.m_ownedChanges) { if (row < m_table.Count) { owned++; } }
            SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.RowsOwnedChanged, owned);
            m_table.m_ownedChanges.Clear();
            if (m_table.m_everything || (m_table.m_pending.Count > 0)) { m_table.Revision = checked((m_table.Revision + 1)); }
            // A publication that owes nothing and leaves the extent alone is the endpoint already published, so a consumer
            // current through it is still current and a frame that carries it stands.
            if (m_table.m_everything || (m_table.m_pending.Count > 0) || (m_table.m_changes.Serial == 0L) || (m_table.m_changes.RowCount != m_table.Count)) {
                m_table.m_changes.Begin(rows: m_table.Count, everything: m_table.m_everything);
                m_table.Cause = (cause ?? (m_table.m_portalChanged ? SdfMeshStagingCause.Portal : null));
            }
            for (var run = 0; (run < m_table.m_pending.Count); run++) {
                var start = m_table.m_pending.Start(index: run);
                var length = Math.Min(val1: (m_table.Count - start), val2: m_table.m_pending.Length(index: run));

                if (length > 0) { m_table.m_changes.Owe(start, length); }
            }
            m_table.m_everything = false;
            m_table.m_portalChanged = false;
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
