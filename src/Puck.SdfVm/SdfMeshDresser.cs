using System.Numerics;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Reconciles a bake's owners into retained row allocations, reading its groups under its frame gate.</summary>
public sealed class SdfMeshDresser : IDisposable {
    private readonly SdfMeshPathBake m_bake;
    private readonly SdfMeshRows m_rows;
    private readonly SdfMeshRows.Writer m_writer;

    private sealed class OwnerState {
        public SdfMeshRows.Run Run;
        public SdfMesh? Mesh;
        public int[] Copies = [];
        // The tier the group's drawn mesh was baked at, which its rows carry (SdfMeshDraw.Tier).
        public int Tier = -1;
        // The mesh the irradiance volume traces in its place, which its rows carry (SdfMeshDraw.Trace).
        public SdfMesh? Trace;
        // The floor the group keeps beside its drawn mesh, which its band rows draw (SdfMeshBand), or null.
        public SdfMesh? Floor;
        public bool Seen;
    }

    private readonly Dictionary<SdfMeshBakeGroup, OwnerState> m_groups = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, OwnerState> m_own = [];
    private readonly List<SdfMeshBakeGroup> m_removedGroups = [];
    private readonly List<int> m_removedOwn = [];
    private readonly GpuRowIndex<(bool Copy, int Index)> m_bindingsByOwner = new();
    private DrawBinding?[] m_bindings = [];
    // The rows whose owner moved this dress, visited in row order so their writes coalesce (SdfMeshRows.Writer.Set).
    private readonly MarkedIndexSet m_candidates = new();
    // Each slot's units (SdfMeshGeometryBuilder.UnitsOf) as every binding riding it was last posed at, NaN once a row is
    // bound to it (ForgetSlotUnits) until its next walk: a slot's rows read the slot's units alone, so a slot that moved
    // at the recorded units reposes nothing and is never walked.
    private float[] m_slotUnits = [];
    private readonly GpuUploadRuns m_slots = new(GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_hostChanges = new(GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_copyRuns = new(GpuRegion.MaxCopyRuns);
    private readonly GpuRowCursor m_slotCursor = new();
    private readonly GpuRowCursor m_hostCursor = new();
    private readonly GpuRowCursor m_copyCursor = new();
    // The copies as the last dress read them, with the revision and frame slot each had, at the copy journal's endpoint
    // m_copyCursor committed.
    private SdfMeshInstance[] m_copies = [];
    private int[] m_copyFrames = [];
    private long[] m_copyRevisions = [];

    private SdfProgram? m_program;

    // The bake's copy layout revision and product revision the last dress reconciled every group at. The group walk
    // stands while both do: the bake moves its product revision on every change to its group list, a group's drawn mesh
    // or the tier it was baked at (SdfMeshPathBake.Replace, Group.Mesh, Group.ShowOwn, FinishReplacements), and its copy
    // layout revision on every change to a group's copies (IndexCopies), which are all a group's rows read.
    private long m_layout = -1;
    private long m_product = -1;

    private SdfMeshRows.Run m_host;
    private bool m_pending;
    private bool m_disposed;
    private bool m_faultThisDress;
    private SdfDressedPickMap? m_map;
    private long m_skipHostThrough;
    // One past the highest slot a group binding rides (DrawBinding.Slot): a moved slot at or past it (a rigid copy's root,
    // which no group binding names) poses no row, so the slot walk stops there.
    private int m_slotEnd;

    /// <summary>Creates a dresser and its private write capability.</summary>
    public SdfMeshDresser(SdfMeshPathBake bake) {
        m_bake = (bake ?? throw new ArgumentNullException(paramName: nameof(bake)));
        (m_rows, m_writer) = SdfMeshRows.Create();
    }

    /// <summary>Gets logical retained scratch and index bytes.</summary>
    public long ScratchBytes => ((((((m_rows.ClipIndexBytes + m_bindingsByOwner.StorageBytes) + m_candidates.StorageBytes + (((long)m_slotUnits.Length) * sizeof(float))) + m_slots.StorageBytes) + m_hostChanges.StorageBytes + m_copyRuns.StorageBytes) + (((long)m_bindings.Length) * IntPtr.Size)) + (((long)m_copies.Length) * (IntPtr.Size + sizeof(int) + sizeof(long))));

    /// <summary>Reconciles products, copy membership and poses without moving unrelated owners.</summary>
    public SdfFrame Dress(SdfFrame frame) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        // A mutable local, not a using variable: the dress releases the access part way (Release), which a using
        // variable's defensive copy would do to the copy alone.
        var access = m_bake.PrepareFrame(frame: frame);

        try {
            return DressHeld(frame: frame, access: ref access);
        } finally {
            access.Dispose();
        }
    }
    private SdfFrame DressHeld(SdfFrame frame, ref SdfMeshPathBake.FrameAccess access) {
        m_faultThisDress |= SdfRowChecks.TakeDressFault();
        var retry = m_pending;

        m_pending = true;
        var forced = SdfRowChecks.TakePack();
        var startOver = (retry || forced || ((m_rows.Count > 0) && ((m_rows.Count - m_rows.LiveCount) > (m_rows.Count / SdfMeshRows.PackingFreeDivisor))) ||
            (frame.TickLocked && (!m_rows.Canonical || CountsChange(access: access, frame: frame))));

        if (startOver) {
            StartOver();
            SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.DressTopologyBuilds);
            SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.TopologyBuildsOf(reason: (retry ? SdfMeshTopologyReason.Retry : (forced ? SdfMeshTopologyReason.Forced : (frame.TickLocked ? SdfMeshTopologyReason.Locked : SdfMeshTopologyReason.FreeShare)))));
            SdfRowChecks.ProcessWork.Count(kind: (retry ? SdfRowChecks.Retry : (frame.TickLocked ? SdfRowChecks.Locked : SdfRowChecks.Packs)));
        }
        // A program sharing the dressed one's field has its groups, slots and copies: only its appearance moved.
        var programChanged = !frame.Program.SharesField(other: m_program);
        var hostNew = (m_host.Count != frame.MeshDraws.Count);
        var hostWhole = m_hostCursor.Collect(frame.MeshDrawChanges, frame.MeshDrawChangesSerial, frame.MeshDraws,
            frame.MeshDraws.Count, frame.MeshDrawsRevision, m_hostChanges, force: (startOver || hostNew));

        if (hostWhole) { SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.HostWhole); }
        _ = m_slotCursor.Collect(frame.MovedTransforms?.Changes, frame.MovedTransformsSerial, frame.DynamicTransforms,
            frame.DynamicTransforms.Count, 0, m_slots, force: (startOver || programChanged));
        // The copies the producer's journal names changed since the last dress, or every copy when it cannot prove that
        // window: m_copyRuns holds the runs to read either way.
        var copiesWhole = m_copyCursor.Collect(frame.MeshCopyChanges, frame.MeshCopyChangesSerial, frame.MeshInstances,
            frame.MeshInstances.Count, 0, m_copyRuns, force: (startOver || programChanged || (frame.MeshCopyChanges is null) || (m_copies.Length != frame.MeshInstances.Count)));
        var matrices = 0;
        var visited = 0;
        var updated = 0;
        var groupsVisited = 0;
        var copiesVisited = 0;

        if (hostNew) {
            var old = m_host;

            m_host = m_writer.Allocate(count: frame.MeshDraws.Count);
            m_writer.Free(run: old);

        }
        for (var r = 0; (r < m_hostChanges.Count); r++) {
            for (var i = m_hostChanges.Start(index: r); (i < (m_hostChanges.Start(index: r) + m_hostChanges.Length(index: r))); i++) {
                var draw = frame.MeshDraws[i];

                Write(m_host, i, draw, new(Host: i));
            }
        }
        if (!startOver && !programChanged && (m_product == access.Revision) && (m_layout == access.CopyLayoutRevision)) {
            SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.DressGroupsStood);
        } else if (startOver || programChanged || access.Whole || (m_product < 0) || (m_layout != access.CopyLayoutRevision)) {
            groupsVisited = access.GroupCount;
            DressGroups(access: in access, frame: frame, matrices: ref matrices, updated: ref updated, visited: ref visited);
        } else {
            // The bake names every group it changed since the last dress (SdfMeshPathBake.Touch), so the rest stand.
            groupsVisited = access.TouchedCount;
            DressTouched(access: in access, frame: frame, matrices: ref matrices, updated: ref updated, visited: ref visited);
        }
        // Every group read is done and the rows claim every mesh they draw: the rest of the dress (copies' own meshes,
        // poses, clips, publishing) reads no group, so the bake's workers publish meanwhile. The row checks read the
        // groups again at the end, and keep the gate.
        if (!SdfRowChecks.Enabled) { access.Release(); }
        // A copy's own mesh: read at every copy the journal names, and dropped where the list ended before it.
        if (copiesWhole) {
            m_removedOwn.Clear();
            foreach (var i in m_own.Keys) { if (i >= frame.MeshInstances.Count) { m_removedOwn.Add(item: i); } }
            foreach (var i in m_removedOwn) { DropOwn(index: i); }
        }
        for (var run = 0; (run < m_copyRuns.Count); run++) {
            for (var i = m_copyRuns.Start(index: run); (i < (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run))); i++) {
                copiesVisited++;
                if (frame.MeshInstances[i].Mesh is not { } mesh) { DropOwn(index: i); continue; }
                if (!m_own.TryGetValue(key: i, value: out var state)) { m_own.Add(key: i, value: state = new()); }
                if (ReferenceEquals(objA: mesh, objB: state.Mesh)) { continue; }
                if (state.Run.Count == 0) { state.Run = m_writer.Allocate(count: 1); }
                RemoveBindings(run: state.Run);
                state.Mesh = mesh;
                Bind(state.Run, 0, Binding(mesh, null, i), frame, ref matrices);
                visited++; updated++;
            }
        }
        m_candidates.Clear();
        if (programChanged) {
            for (var row = 0; (row < m_bindings.Length); row++) { if (m_bindings[row] is not null) { _ = m_candidates.Add(index: row); } }
        }
        var slotsSpared = 0;

        for (var run = 0; (run < m_slots.Count); run++) {
            var end = Math.Min(val1: (m_slots.Start(index: run) + m_slots.Length(index: run)), val2: m_slotEnd);

            for (var slot = m_slots.Start(index: run); (slot < end); slot++) {
                // A row placed by its slot reads the slot's units and nothing else of it (DrawBinding.Repose): a slot that
                // moved at the units its bindings were posed at leaves every one of them standing.
                var units = ((slot < frame.DynamicTransforms.Count) ? SdfMeshGeometryBuilder.UnitsOf(transform: frame.DynamicTransforms[slot]) : float.NaN);

                if (!programChanged && !startOver && (m_slotUnits[slot] == units)) {
                    slotsSpared++;
                    continue;
                }
                m_slotUnits[slot] = units;
                AddCandidates(key: (false, slot));
            }
        }
        for (var run = 0; (run < m_copyRuns.Count); run++) {
            for (var i = m_copyRuns.Start(index: run); (i < (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run))); i++) {
                if (programChanged || (i >= m_copies.Length) || !ReferenceEquals(objA: m_copies[i], objB: frame.MeshInstances[i]) || (m_copyRevisions[i] != frame.MeshInstances[i].Revision) || (m_copyFrames[i] != frame.MeshInstances[i].FrameSlot)) { AddCandidates(key: (true, i)); }
            }
        }
        m_candidates.Sort();
        foreach (var row in m_candidates.Members) {
            if ((row >= m_bindings.Length) || (m_bindings[row] is not { } binding)) { continue; }
            visited++;
            // A draw placed by its slot whose slot only moved poses as it did: its row stands, and the kernels and
            // every CPU reader place it from the slot's row (SdfMeshDraw.PlacementIn).
            if (!binding.Repose(frame: frame, matrices: ref matrices) && !programChanged) { continue; }

            var (run, offset) = BindingRun(row: row);
            Write(run, offset, in binding.Draw, OwnerOf(binding: binding));
        }
        m_writer.SetClips(clips: frame.SlotClips);
        // If clip-copy reconciliation allocated or freed
        // rows on an otherwise canonical locked frame, reconcile once from empty before publishing it.
        if (frame.TickLocked && !startOver && (m_program is not null) && !m_rows.Canonical) {
            m_pending = false;
            return Dress(frame: frame);
        }
        if (SdfRowChecks.Enabled) { CheckDress(access: access, frame: frame); }
        m_writer.Publish(cause: (startOver ? (retry ? SdfMeshStagingCause.Retry : SdfMeshStagingCause.Rebuild) : null));
        if ((m_map is null) || !ReferenceEquals(objA: m_map.Inner, objB: frame.HostPickMap)) { m_map = ((frame.HostPickMap is { } inner) ? new SdfDressedPickMap(inner: inner, rows: m_rows) : null); }
        // The frame answers with this dresser's rows and map by reference, so the frame handed in is the frame answered
        // and no copy is made. A frame is dressed by one dresser: a residency dresses the copy its own writer holds, which
        // carries no dressing (SdfFrame.Writer.CopyFrom).
        frame.AdoptDressing(rows: m_rows, picks: m_map);

        // A whole answer resizes to the list (a count change always answers whole), and every answer rewrites the copies
        // its runs name, so m_copies is the list at the endpoint the cursor commits.
        if (m_copies.Length != frame.MeshInstances.Count) { m_copies = new SdfMeshInstance[frame.MeshInstances.Count]; m_copyFrames = new int[m_copies.Length]; m_copyRevisions = new long[m_copies.Length]; }
        for (var run = 0; (run < m_copyRuns.Count); run++) {
            for (var i = m_copyRuns.Start(index: run); (i < (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run))); i++) { m_copies[i] = frame.MeshInstances[i]; m_copyFrames[i] = m_copies[i].FrameSlot; m_copyRevisions[i] = m_copies[i].Revision; }
        }
        if (SdfRowChecks.SkipHostRequested) { m_skipHostThrough = ((m_rows.Serial + SdfMovedTransforms.History) + 2); SdfRowChecks.SkipHostRequested = false; }
        if (m_rows.Serial <= m_skipHostThrough) { SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.HostSkipped); } else { m_hostCursor.Commit(); }
        m_slotCursor.Commit();
        m_copyCursor.Commit();
        m_program = frame.Program;
        m_layout = access.CopyLayoutRevision;
        m_product = access.Revision;
        m_pending = false;
        access.Acknowledge();

        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressBindingsVisited, visited);
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressMatricesComputed, matrices);
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressedRecordsUpdated, updated);
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressGroupsVisited, groupsVisited);
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressCopiesVisited, copiesVisited);
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressSlotsSpared, slotsSpared);
        return frame;
    }
    // Reconciles every group the bake lists with the mesh it draws, its copies and the tier its mesh was baked at,
    // rebinding a group's rows only where one of those changed, and frees the rows of groups the bake no longer lists.
    private void DressGroups(in SdfMeshPathBake.FrameAccess access, SdfFrame frame, ref int matrices, ref int visited, ref int updated) {
        foreach (var state in m_groups.Values) { state.Seen = false; }
        for (var g = 0; (g < access.GroupCount); g++) {
            var group = access.GroupAt(index: g);

            if (access.MeshOf(group: group) is not { } mesh) { continue; }
            DressGroup(access: in access, frame: frame, group: group, matrices: ref matrices, mesh: mesh, updated: ref updated, visited: ref visited);
        }
        m_removedGroups.Clear();
        foreach (var (group, state) in m_groups) { if (!state.Seen) { RemoveBindings(run: state.Run); m_writer.Free(run: state.Run); m_removedGroups.Add(item: group); } }
        foreach (var group in m_removedGroups) { _ = m_groups.Remove(key: group); }
    }
    // Reconciles only the groups the bake names changed since the last dress (SdfMeshPathBake.FrameAccess.TouchedAt): a
    // group still listed with a mesh to draw as DressGroups does, and any other gives up the rows it held.
    private void DressTouched(in SdfMeshPathBake.FrameAccess access, SdfFrame frame, ref int matrices, ref int visited, ref int updated) {
        for (var t = 0; (t < access.TouchedCount); t++) {
            var group = access.TouchedAt(index: t);

            if (access.Lists(group: group) && (access.MeshOf(group: group) is { } mesh)) {
                DressGroup(access: in access, frame: frame, group: group, matrices: ref matrices, mesh: mesh, updated: ref updated, visited: ref visited);
                continue;
            }
            if (m_groups.Remove(key: group, value: out var state)) {
                RemoveBindings(run: state.Run);
                m_writer.Free(run: state.Run);
            }
        }
    }
    // Rebinds a group's rows where its mesh, its floor, its place in the copies, its tier or its trace changed.
    private void DressGroup(in SdfMeshPathBake.FrameAccess access, SdfFrame frame, SdfMeshBakeGroup group, SdfMesh mesh, ref int matrices, ref int visited, ref int updated) {
        if (!m_groups.TryGetValue(key: group, value: out var state)) { m_groups.Add(key: group, value: state = new()); }
        state.Seen = true;
        var copies = access.CopiesOf(group: group);
        var floor = access.FloorOf(group: group);
        var count = RowsOf(copies: copies, floor: floor, mesh: mesh);
        var tier = access.TierOf(group: group);
        var trace = access.TraceOf(group: group);
        var changed = (!ReferenceEquals(objA: mesh, objB: state.Mesh) || !ReferenceEquals(objA: floor, objB: state.Floor) || (state.Run.Count != count) || (state.Tier != tier) ||
            !ReferenceEquals(objA: trace, objB: state.Trace) || ((m_layout != access.CopyLayoutRevision) && !state.Copies.SequenceEqual(second: (copies ?? Array.Empty<int>()))));

        if (!changed) { return; }
        var old = state.Run;

        if (old.Count != count) { state.Run = m_writer.Allocate(count: count); }
        RemoveBindings(run: old);
        state.Mesh = mesh;
        state.Floor = floor;
        state.Tier = tier;
        state.Trace = trace;
        state.Copies = (copies?.ToArray() ?? []);

        BindingsOf(access: in access, copies: copies, floor: floor, group: group, into: m_groupBindings, mesh: mesh, pooled: true, run: state.Run, tier: tier, trace: trace);
        foreach (var (offset, binding) in m_groupBindings) {
            Bind(binding: binding, frame: frame, matrices: ref matrices, offset: offset, run: state.Run);
            visited++; updated++;
        }
        m_groupBindings.Clear();
        if (old != state.Run) { m_writer.Free(run: old); }
    }
    // The rows a group's placements take: each placement (each copy, or the group once) draws its mesh's pieces and,
    // where the group keeps a floor beside it (SdfMeshPathBake.FrameAccess.FloorOf), the floor's pieces after them.
    private static int RowsOf(SdfMesh mesh, SdfMesh? floor, IReadOnlyList<int>? copies) => ((mesh.PieceCount + (floor?.PieceCount ?? 0)) * (copies?.Count ?? 1));
    // Each row of a group's run and the binding it holds, in RowsOf's layout. Where the group keeps a floor, every row of
    // a placement takes one band (SdfMeshBand): the sphere about the box holding both meshes and the floor's cell, its
    // mesh's pieces as the fine tier naming the placement's floor rows, and the floor's pieces as the floor. Appends to
    // into; a dress draws its bindings from the kept ones (pooled, Binding), the row checks' rebuild makes its own.
    private void BindingsOf(in SdfMeshPathBake.FrameAccess access, SdfMeshBakeGroup group, SdfMesh mesh, SdfMesh? floor,
        IReadOnlyList<int>? copies, SdfMeshRows.Run run, int tier, SdfMesh? trace, List<(int Offset, DrawBinding Binding)> into, bool pooled) {
        var pieces = mesh.PieceCount;
        var floorPieces = (floor?.PieceCount ?? 0);
        var stride = (pieces + floorPieces);
        var placements = (copies?.Count ?? 1);
        var chunked = ((copies is null) && (mesh.Chunks.Count > 0));
        var band = ((floor is null) ? SdfMeshBand.None : SdfMeshBand.Of(
            floorTier: access.FloorTierOf(group: group),
            high: Vector3.Max(value1: mesh.PositionHigh, value2: floor.PositionHigh),
            low: Vector3.Min(value1: mesh.PositionLow, value2: floor.PositionLow)
        ));
        var floorTier = ((floor is null) ? tier : access.FloorTierOf(group: group));

        for (var placement = 0; (placement < placements); placement++) {
            var copy = ((copies is null) ? -1 : copies[placement]);
            var first = (placement * stride);

            for (var piece = 0; (piece < pieces); piece++) {
                var fine = ((floor is null) ? band : (band with { FloorRow = (run.Start + first + pieces), FloorRows = floorPieces, Role = SdfMeshBandRole.Fine }));

                into.Add(item: ((first + piece), Make(band: fine, chunked: chunked, copyIndex: copy, mesh: mesh.Piece(index: piece), piece: piece, tier: tier, trace: trace)));
            }
            for (var piece = 0; (piece < floorPieces); piece++) {
                into.Add(item: ((first + pieces + piece), Make(band: (band with { Role = SdfMeshBandRole.Floor }), chunked: (chunked && (floor!.Chunks.Count > 0)), copyIndex: copy,
                    mesh: floor!.Piece(index: piece), piece: piece, tier: floorTier, trace: null)));
            }
        }

        DrawBinding Make(SdfMesh mesh, int copyIndex, bool chunked, int tier, SdfMesh? trace, int piece, SdfMeshBand band) => (pooled
            ? Binding(band: band, chunked: chunked, copyIndex: copyIndex, group: group, mesh: mesh, piece: piece, tier: tier, trace: trace)
            : new DrawBinding(band: band, chunked: chunked, copyIndex: copyIndex, group: group, mesh: mesh, piece: piece, tier: tier, trace: trace));
    }
    // The bindings a group dress binds, kept across dresses so it allocates no list.
    private readonly List<(int Offset, DrawBinding Binding)> m_groupBindings = [];
    // Lets go of the rows a copy's own mesh held at an index, if any.
    private void DropOwn(int index) {
        if (!m_own.Remove(key: index, value: out var state)) { return; }
        RemoveBindings(run: state.Run);
        m_writer.Free(run: state.Run);
    }

    private void Write(SdfMeshRows.Run run, int offset, in SdfMeshDraw draw, SdfMeshRows.Owner owner) {
        m_writer.Set(draw: draw, offset: offset, owner: owner, run: run);
        if (m_faultThisDress) { m_faultThisDress = false; throw new SdfDressFaultException(); }
    }
    private bool CountsChange(SdfFrame frame, SdfMeshPathBake.FrameAccess access) {
        if (m_host.Count != frame.MeshDraws.Count) { return true; }
        var finished = 0;

        for (var g = 0; (g < access.GroupCount); g++) {
            var group = access.GroupAt(index: g);

            if (access.MeshOf(group: group) is not { } mesh) { continue; }
            finished++;
            if (!m_groups.TryGetValue(key: group, value: out var state) || (state.Run.Count != RowsOf(copies: access.CopiesOf(group: group), floor: access.FloorOf(group: group), mesh: mesh))) { return true; }
        }
        if (finished != m_groups.Count) { return true; }
        var own = 0;

        for (var i = 0; (i < frame.MeshInstances.Count); i++) { if (frame.MeshInstances[i].Mesh is not null) { own++; if (!m_own.ContainsKey(key: i)) { return true; } } }
        return (own != m_own.Count);
    }
    private void StartOver() {
        m_writer.StartOver();
        m_groups.Clear(); m_own.Clear(); m_bindingsByOwner.Clear();
        foreach (var binding in m_bindings) { if (binding is not null) { m_releasedBindings.Push(item: binding.Release()); } }
        Array.Clear(array: m_bindings);
        m_host = default; m_program = null; m_layout = -1; m_product = -1; m_slotEnd = 0;
    }
    private void RemoveBindings(SdfMeshRows.Run run) {
        for (var row = run.Start; (row < (run.Start + run.Count)); row++) {
            if (row >= m_bindings.Length) { continue; }
            if (m_bindings[row] is { } released) { m_releasedBindings.Push(item: released.Release()); }
            m_bindings[row] = null;
            m_bindingsByOwner.Remove(row: row);
        }
    }
    // The bindings rows let go of, which only this stack holds once removed: the next bind takes one and resets it.
    private readonly Stack<DrawBinding> m_releasedBindings = new();

    private DrawBinding Binding(SdfMesh mesh, SdfMeshBakeGroup? group, int copyIndex, bool chunked = false, int tier = -1, SdfMesh? trace = null, int piece = 0, SdfMeshBand band = default) =>
        (m_releasedBindings.TryPop(result: out var released)
            ? released.Reset(chunked: chunked, copyIndex: copyIndex, group: group, mesh: mesh, piece: piece, tier: tier, trace: trace, band: band)
            : new DrawBinding(chunked: chunked, copyIndex: copyIndex, group: group, mesh: mesh, piece: piece, tier: tier, trace: trace, band: band));
    private void Bind(SdfMeshRows.Run run, int offset, DrawBinding binding, SdfFrame frame, ref int matrices) {
        var row = (run.Start + offset);

        ArrayCapacity.EnsureLength(ref m_bindings, row + 1);
        m_bindings[row] = binding;
        binding.Run = run;
        IndexBinding(binding: binding, row: row);
        _ = binding.Repose(frame: frame, matrices: ref matrices);
        Write(run, offset, in binding.Draw, OwnerOf(binding: binding));
    }
    private static SdfMeshRows.Owner OwnerOf(DrawBinding binding) => new(Instance: binding.FirstInstance, RigidCopy: (binding.CopyIndex >= 0));
    private void IndexBinding(int row, DrawBinding binding) {
        m_bindingsByOwner.Remove(row: row);
        if (binding.CopyIndex >= 0) { m_bindingsByOwner.Set(row, (true, binding.CopyIndex)); } else if (binding.Slot is { } slot) { m_bindingsByOwner.Set(key: (false, slot), row: row); m_slotEnd = Math.Max(val1: m_slotEnd, val2: (slot + 1)); ForgetSlotUnits(slot: slot); }
    }
    // A row bound to a slot is posed at the slot's units as they are now, which the slot's record (m_slotUnits) may not
    // hold: the record is forgotten, so the slot's next move walks its rows once and records its units again.
    private void ForgetSlotUnits(int slot) {
        if (slot >= m_slotUnits.Length) {
            var grown = m_slotUnits.Length;

            Array.Resize(array: ref m_slotUnits, newSize: ArrayCapacity.Grown(grown, (slot + 1)));
            m_slotUnits.AsSpan(start: grown).Fill(value: float.NaN);
        }
        m_slotUnits[slot] = float.NaN;
    }
    private void AddCandidates((bool Copy, int Index) key) {
        for (var row = m_bindingsByOwner.First(key: key); (row >= 0); row = m_bindingsByOwner.Next(row: row)) { _ = m_candidates.Add(index: row); }
    }
    private (SdfMeshRows.Run Run, int Offset) BindingRun(int row) => (m_bindings[row]!.Run, (row - m_bindings[row]!.Run.Start));
    private void CheckDress(SdfFrame frame, SdfMeshPathBake.FrameAccess access) {
        var expected = new List<SdfMeshDraw>(collection: frame.MeshDraws);
        var rebuilt = new List<(int Offset, DrawBinding Binding)>();
        var matrices = 0;

        for (var g = 0; (g < access.GroupCount); g++) {
            var group = access.GroupAt(index: g);

            if (access.MeshOf(group: group) is not { } mesh) { continue; }
            // A band's floor rows are named by where the group's run stands, which the rebuild takes from the run it holds.
            var run = (m_groups.TryGetValue(key: group, value: out var state) ? state.Run : default);

            BindingsOf(access: in access, copies: access.CopiesOf(group: group), floor: access.FloorOf(group: group), group: group, into: rebuilt, mesh: mesh, pooled: false, run: run,
                tier: access.TierOf(group: group), trace: access.TraceOf(group: group));
            foreach (var (_, binding) in rebuilt) {
                _ = binding.Repose(frame: frame, matrices: ref matrices);
                expected.Add(item: binding.Draw);
            }
            rebuilt.Clear();
        }
        for (var i = 0; (i < frame.MeshInstances.Count); i++) { if (frame.MeshInstances[i].Mesh is { } mesh) { var binding = new DrawBinding(mesh, null, i); _ = binding.Repose(frame: frame, matrices: ref matrices); expected.Add(item: binding.Draw); } }
        SdfSlotClip.Apply(expected, frame.SlotClips);
        foreach (var (row, draw) in m_rows.Live()) {
            var at = expected.FindIndex(match: candidate => candidate.SameStagedContent(other: in draw));

            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Dress);
            if (at < 0) { SdfRowChecks.DressFailure(message: $"unexpected row {row}, owner {m_rows.OwnerOf(row: row)}"); } else { expected.RemoveAt(index: at); }
        }
        if (expected.Count != 0) { SdfRowChecks.DressFailure(message: $"missing {expected.Count} rebuilt rows"); }
    }

    /// <summary>Releases the row table's mesh claims.</summary>
    public void Dispose() { if (m_disposed) { return; } m_disposed = true; StartOver(); m_releasedBindings.Clear(); }

    // A row's draw: one piece of a group's mesh, or a copy's own mesh, the tier the group's drawn mesh was baked at, and
    // the mesh the irradiance volume traces in the placement's place with the piece the row draws. A moving group's and a
    // moving copy's draws are placed by their slots (SdfMeshDraw.PlacedBySlot): the draw keeps their scale, so the slot
    // moving leaves it standing.
    // A row is rebound whenever its group's mesh, tier, trace or copies move, which a retier does to many rows a frame,
    // so a binding a dress lets go of (Release) is kept and bound again (Reset) rather than allocated anew. Reset writes
    // every field, so a reused binding answers exactly as a new one would.
    private sealed class DrawBinding {
        private SdfMesh m_mesh = null!;
        private SdfMeshBakeGroup? m_group;
        private int m_copyIndex;
        private bool m_chunked;
        private int m_tier;
        private SdfMesh? m_trace;
        private int m_piece;
        // The detail band the row's draw stands in (SdfMeshBand).
        private SdfMeshBand m_band;

        public DrawBinding(SdfMesh mesh, SdfMeshBakeGroup? group, int copyIndex, bool chunked = false, int tier = -1, SdfMesh? trace = null, int piece = 0, SdfMeshBand band = default) =>
            Reset(band: band, chunked: chunked, copyIndex: copyIndex, group: group, mesh: mesh, piece: piece, tier: tier, trace: trace);

        // Makes this binding the one the constructor would make of the same arguments.
        public DrawBinding Reset(SdfMesh mesh, SdfMeshBakeGroup? group, int copyIndex, bool chunked = false, int tier = -1, SdfMesh? trace = null, int piece = 0, SdfMeshBand band = default) {
            (m_mesh, m_group, m_copyIndex, m_chunked, m_tier, m_trace, m_piece, m_band) = (mesh, group, copyIndex, chunked, tier, trace, piece, band);
            (m_motionSource, m_motionIdentity, m_copy, m_copyRevision, m_pickIdentity, m_draw, m_posed, m_scale, Run) = (null, null, null, 0L, null, default, false, 1f, default);
            return this;
        }
        // Lets go of every mesh, group and copy the binding names, so a kept binding holds nothing alive.
        public DrawBinding Release() {
            (m_mesh, m_group, m_trace, m_band) = (null!, null, null, default);
            (m_motionSource, m_motionIdentity, m_copy, m_pickIdentity, m_draw) = (null, null, null, null, default);
            return this;
        }
        private object? m_motionSource;
        private object? m_motionIdentity;
        private SdfMeshInstance? m_copy;
        private long m_copyRevision;
        private SdfMeshCopySurface? m_pickIdentity;
        private SdfMeshDraw m_draw;
        private bool m_posed;
        private float m_scale = 1f;

        public SdfMeshRows.Run Run;

        public int CopyIndex => m_copyIndex;
        public int FirstInstance => (m_group?.FirstInstance ?? -1);
        public int? Slot => ((m_group is { Dynamic: true }) ? m_group.Slot : null);

        // The draw the row holds as last posed (Repose); read in place, since an unchanged draw is never copied out.
        public ref readonly SdfMeshDraw Draw => ref m_draw;

        // Poses the row's draw for the frame, answering whether it differs from the draw last posed. A copy's draw changes
        // only with the copy's revision, which a moving copy's pose leaves standing (SdfMeshInstance.Revision), and a
        // moving group's only with its slot's scale.
        public bool Repose(SdfFrame frame, ref int matrices) {
            if (m_copyIndex >= 0) {
                var copy = frame.MeshInstances[m_copyIndex];
                var firstInstance = (m_group?.FirstInstance ?? -1);

                if (ReferenceEquals(objA: copy, objB: m_copy) && (m_copyRevision == copy.Revision) && (m_draw.FrameSlot == copy.FrameSlot) && (m_pickIdentity?.FirstInstance == firstInstance)) {
                    return false;
                }
                m_copy = copy;
                m_copyRevision = copy.Revision;
                if ((m_pickIdentity is null) || !Equals(objA: m_pickIdentity.Identity, objB: copy.Identity) || (m_pickIdentity.FirstInstance != firstInstance)) {
                    m_pickIdentity = new SdfMeshCopySurface(copy.Identity, firstInstance);
                }
                var source = (copy.MotionIdentity ?? copy.Identity);

                if (!Equals(objA: m_motionSource, objB: source)) {
                    m_motionSource = source;
                    m_motionIdentity = ((source, (((object?)m_group?.Selector) ?? "mesh")));
                }
                matrices++;
                m_draw = new SdfMeshDraw(identity: m_pickIdentity, material: ((m_group is null) ? copy.Material : 0),
                    mesh: ((m_group is null) ? copy.Mesh! : m_mesh), objectToWorld: copy.Matrix) {
                    Audience = copy.Audience,
                    FrameSlot = copy.FrameSlot,
                    Glass = (m_group?.Glass ?? false),
                    CastsNoShadow = (m_group?.CastsNoShadow ?? false),
                    IsDynamic = copy.IsDynamic,
                    MotionIdentity = m_motionIdentity,
                    // Every group and piece of one placed copy is one object.
                    ObjectKey = CopyObjectKey(copy: m_copyIndex),
                    Piece = m_piece,
                    Tier = ((m_group is null) ? -1 : m_tier),
                    Trace = m_trace,
                    Band = m_band,
                    UsesRootMaterialFrame = copy.UsesRootMaterialFrame,
                };
                return true;
            }
            var scale = (((m_group!.Dynamic) && (m_group.Slot < frame.DynamicTransforms.Count)) ? SdfMeshGeometryBuilder.UnitsOf(transform: frame.DynamicTransforms[m_group.Slot]) : m_scale);

            if (m_posed && (m_scale == scale) && (m_draw.FrameSlot == (m_group.Dynamic ? m_group.Slot : null)) && (m_draw.Glass == m_group.Glass) &&
                (m_draw.CastsNoShadow == m_group.CastsNoShadow) && (m_draw.Displacement == m_group.Displacement)) {
                return false;
            }
            m_scale = scale;
            m_posed = true;
            if (m_group.Dynamic) { matrices++; }

            m_draw = new SdfMeshDraw(identity: (m_chunked ? m_mesh : m_group), material: 0, mesh: m_mesh,
                objectToWorld: (m_group.Dynamic ? Matrix4x4.CreateScale(scale: scale) : Matrix4x4.Identity)) {
                FrameSlot = (m_group.Dynamic ? m_group.Slot : null),
                Glass = m_group.Glass,
                CastsNoShadow = m_group.CastsNoShadow,
                // A displaced surface is never static (SdfMeshDraw.IsStatic), whatever its placement does.
                Displacement = m_group.Displacement,
                IsDynamic = m_group.Dynamic,
                // Every piece and tier of one bake group is one object.
                ObjectKey = GroupObjectKey(firstInstance: m_group.FirstInstance),
                Piece = m_piece,
                Tier = m_tier,
                Trace = m_trace,
                Band = m_band,
            };
            return true;
        }

        // The object keys (SdfMeshDraw.ObjectKey): a placed copy's from its index in the frame's copies, a bake group's
        // from its first program instance, in two halves of the key's range so no copy shares a group's.
        private const uint GroupObjectKeys = ((SdfMeshRecord.ObjectKeyMask >> 1) + 1u);
        private static uint CopyObjectKey(int copy) => (1u + (((uint)copy) % (GroupObjectKeys - 1u)));
        private static uint GroupObjectKey(int firstInstance) => (GroupObjectKeys | (((uint)firstInstance) & (GroupObjectKeys - 1u)));
    }

}
