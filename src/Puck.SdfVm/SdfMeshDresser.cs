using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Reconciles a bake's owners into retained row allocations, under its frame gate.</summary>
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
        public bool Seen;
    }

    private readonly Dictionary<SdfMeshBakeGroup, OwnerState> m_groups = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, OwnerState> m_own = [];
    private readonly List<SdfMeshBakeGroup> m_removedGroups = [];
    private readonly List<int> m_removedOwn = [];
    private readonly GpuRowIndex<(bool Copy, int Index)> m_bindingsByOwner = new();
    private DrawBinding?[] m_bindings = [];
    private readonly GpuUploadRuns m_candidates = new(GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_slots = new(GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_hostChanges = new(GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_copyRuns = new(GpuRegion.MaxCopyRuns);
    private readonly GpuRowCursor m_slotCursor = new();
    private readonly GpuRowCursor m_hostCursor = new();
    private readonly GpuRowCursor m_copyCursor = new();
    // The copies as the last dress read them, at the copy journal's endpoint m_copyCursor committed.
    private SdfMeshInstance[] m_copies = [];
    private int[] m_copyFrames = [];

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

    /// <summary>Creates a dresser and its private write capability.</summary>
    public SdfMeshDresser(SdfMeshPathBake bake) {
        m_bake = (bake ?? throw new ArgumentNullException(paramName: nameof(bake)));
        (m_rows, m_writer) = SdfMeshRows.Create();
    }

    /// <summary>Gets logical retained scratch and index bytes.</summary>
    public long ScratchBytes => ((((((m_rows.ClipIndexBytes + m_bindingsByOwner.StorageBytes) + m_candidates.StorageBytes) + m_slots.StorageBytes) + m_hostChanges.StorageBytes + m_copyRuns.StorageBytes) + (((long)m_bindings.Length) * IntPtr.Size)) + (((long)m_copies.Length) * (IntPtr.Size + sizeof(int))));

    /// <summary>Reconciles products, copy membership and poses without moving unrelated owners.</summary>
    public SdfFrame Dress(SdfFrame frame) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        using var access = m_bake.PrepareFrame(frame: frame);

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
        } else {
            groupsVisited = access.GroupCount;
            DressGroups(access: in access, frame: frame, matrices: ref matrices, updated: ref updated, visited: ref visited);
        }
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
                Bind(state.Run, 0, new DrawBinding(mesh, null, i), frame, ref matrices);
                visited++; updated++;
            }
        }
        m_candidates.Clear();
        if (programChanged) {
            for (var row = 0; (row < m_bindings.Length); row++) { if (m_bindings[row] is not null) { m_candidates.Add(length: 1, start: row); } }
        }
        for (var run = 0; (run < m_slots.Count); run++) {
            for (var slot = m_slots.Start(index: run); (slot < (m_slots.Start(index: run) + m_slots.Length(index: run))); slot++) { AddCandidates(key: (false, slot)); }
        }
        for (var run = 0; (run < m_copyRuns.Count); run++) {
            for (var i = m_copyRuns.Start(index: run); (i < (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run))); i++) {
                if (programChanged || (i >= m_copies.Length) || !ReferenceEquals(objA: m_copies[i], objB: frame.MeshInstances[i]) || (m_copyFrames[i] != frame.MeshInstances[i].FrameSlot)) { AddCandidates(key: (true, i)); }
            }
        }
        for (var r = 0; (r < m_candidates.Count); r++) {
            for (var row = m_candidates.Start(index: r); (row < (m_candidates.Start(index: r) + m_candidates.Length(index: r))); row++) {
                if ((row >= m_bindings.Length) || (m_bindings[row] is not { } binding)) { continue; }
                var draw = binding.Pose(frame: frame, matrices: ref matrices);

                var (run, offset) = BindingRun(row: row);
                Write(run, offset, draw, OwnerOf(binding: binding));
                IndexBinding(binding: binding, row: row);
                visited++;
            }
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
        // The frame answers with this dresser's rows and map by reference, so a frame that stands is the frame answered
        // and no copy is made. A frame another dresser dressed is copied first: its rows are theirs to keep.
        var result = (((frame.MeshRows is null) || ReferenceEquals(objA: frame.MeshRows, objB: m_rows)) ? frame : (frame with { }));

        result.AdoptDressing(rows: m_rows, picks: m_map);

        // A whole answer resizes to the list (a count change always answers whole), and every answer rewrites the copies
        // its runs name, so m_copies is the list at the endpoint the cursor commits.
        if (m_copies.Length != frame.MeshInstances.Count) { m_copies = new SdfMeshInstance[frame.MeshInstances.Count]; m_copyFrames = new int[m_copies.Length]; }
        for (var run = 0; (run < m_copyRuns.Count); run++) {
            for (var i = m_copyRuns.Start(index: run); (i < (m_copyRuns.Start(index: run) + m_copyRuns.Length(index: run))); i++) { m_copies[i] = frame.MeshInstances[i]; m_copyFrames[i] = m_copies[i].FrameSlot; }
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
        return result;
    }
    // Reconciles every group the bake lists with the mesh it draws, its copies and the tier its mesh was baked at,
    // rebinding a group's rows only where one of those changed, and frees the rows of groups the bake no longer lists.
    private void DressGroups(in SdfMeshPathBake.FrameAccess access, SdfFrame frame, ref int matrices, ref int visited, ref int updated) {
        foreach (var state in m_groups.Values) { state.Seen = false; }
        for (var g = 0; (g < access.GroupCount); g++) {
            var group = access.GroupAt(index: g);

            if (access.MeshOf(group: group) is not { } mesh) { continue; }
            if (!m_groups.TryGetValue(key: group, value: out var state)) { m_groups.Add(key: group, value: state = new()); }
            state.Seen = true;
            var copies = access.CopiesOf(group: group);
            var count = (mesh.PieceCount * (copies?.Count ?? 1));
            var tier = access.TierOf(group: group);
            var trace = access.TraceOf(group: group);
            var changed = (!ReferenceEquals(objA: mesh, objB: state.Mesh) || (state.Run.Count != count) || (state.Tier != tier) || !ReferenceEquals(objA: trace, objB: state.Trace) ||
                ((m_layout != access.CopyLayoutRevision) && !state.Copies.SequenceEqual(second: (copies ?? Array.Empty<int>()))));

            if (!changed) { continue; }
            var old = state.Run;

            if (old.Count != count) { state.Run = m_writer.Allocate(count: count); }
            RemoveBindings(run: old);
            state.Mesh = mesh;
            state.Tier = tier;
            state.Trace = trace;
            state.Copies = (copies?.ToArray() ?? []);

            for (var offset = 0; (offset < count); offset++) {
                var binding = new DrawBinding(mesh.Piece(index: (offset % mesh.PieceCount)), group,
                    ((copies is null) ? -1 : copies[(offset / mesh.PieceCount)]), ((copies is null) && (mesh.Chunks.Count > 0)), tier, trace, (offset % mesh.PieceCount));

                Bind(binding: binding, frame: frame, matrices: ref matrices, offset: offset, run: state.Run);
                visited++; updated++;
            }
            if (old != state.Run) { m_writer.Free(run: old); }
        }
        m_removedGroups.Clear();
        foreach (var (group, state) in m_groups) { if (!state.Seen) { RemoveBindings(run: state.Run); m_writer.Free(run: state.Run); m_removedGroups.Add(item: group); } }
        foreach (var group in m_removedGroups) { _ = m_groups.Remove(key: group); }
    }
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
            if (!m_groups.TryGetValue(key: group, value: out var state) || (state.Run.Count != (mesh.PieceCount * (access.CopiesOf(group: group)?.Count ?? 1)))) { return true; }
        }
        if (finished != m_groups.Count) { return true; }
        var own = 0;

        for (var i = 0; (i < frame.MeshInstances.Count); i++) { if (frame.MeshInstances[i].Mesh is not null) { own++; if (!m_own.ContainsKey(key: i)) { return true; } } }
        return (own != m_own.Count);
    }
    private void StartOver() {
        m_writer.StartOver();
        m_groups.Clear(); m_own.Clear(); m_bindingsByOwner.Clear(); Array.Clear(array: m_bindings);
        m_host = default; m_program = null; m_layout = -1; m_product = -1;
    }
    private void RemoveBindings(SdfMeshRows.Run run) {
        for (var row = run.Start; (row < (run.Start + run.Count)); row++) { if (row < m_bindings.Length) { m_bindings[row] = null; m_bindingsByOwner.Remove(row: row); } }
    }
    private void Bind(SdfMeshRows.Run run, int offset, DrawBinding binding, SdfFrame frame, ref int matrices) {
        var row = (run.Start + offset);

        if (m_bindings.Length <= row) { Array.Resize(array: ref m_bindings, newSize: Math.Max(val1: (row + 1), val2: (m_bindings.Length * 2))); }
        m_bindings[row] = binding;
        binding.Run = run;
        IndexBinding(binding: binding, row: row);
        Write(run, offset, binding.Pose(frame: frame, matrices: ref matrices), OwnerOf(binding: binding));
    }
    private static SdfMeshRows.Owner OwnerOf(DrawBinding binding) => new(Instance: binding.FirstInstance, RigidCopy: (binding.CopyIndex >= 0));
    private void IndexBinding(int row, DrawBinding binding) {
        m_bindingsByOwner.Remove(row: row);
        if (binding.CopyIndex >= 0) { m_bindingsByOwner.Set(row, (true, binding.CopyIndex)); } else if (binding.Slot is { } slot) { m_bindingsByOwner.Set(key: (false, slot), row: row); }
    }
    private void AddCandidates((bool Copy, int Index) key) {
        for (var row = m_bindingsByOwner.First(key: key); (row >= 0); row = m_bindingsByOwner.Next(row: row)) { m_candidates.Add(length: 1, start: row); }
    }
    private (SdfMeshRows.Run Run, int Offset) BindingRun(int row) => (m_bindings[row]!.Run, (row - m_bindings[row]!.Run.Start));
    private void CheckDress(SdfFrame frame, SdfMeshPathBake.FrameAccess access) {
        var expected = new List<SdfMeshDraw>(collection: frame.MeshDraws);
        var matrices = 0;

        for (var g = 0; (g < access.GroupCount); g++) {
            var group = access.GroupAt(index: g);

            if (access.MeshOf(group: group) is not { } mesh) { continue; }
            var copies = access.CopiesOf(group: group);

            for (var c = 0; (c < (copies?.Count ?? 1)); c++) {
                for (var p = 0; (p < mesh.PieceCount); p++) { expected.Add(item: new DrawBinding(mesh.Piece(index: p), group, ((copies is null) ? -1 : copies[c]), ((copies is null) && (mesh.Chunks.Count > 0)), access.TierOf(group: group), access.TraceOf(group: group), p).Pose(frame: frame, matrices: ref matrices)); }
            }
        }
        for (var i = 0; (i < frame.MeshInstances.Count); i++) { if (frame.MeshInstances[i].Mesh is { } mesh) { expected.Add(item: new DrawBinding(mesh, null, i).Pose(frame: frame, matrices: ref matrices)); } }
        SdfSlotClip.Apply(expected, frame.SlotClips);
        foreach (var (row, draw) in m_rows.Live()) {
            var at = expected.FindIndex(match: candidate => candidate.SameStagedContent(other: in draw));

            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Dress);
            if (at < 0) { SdfRowChecks.DressFailure(message: $"unexpected row {row}, owner {m_rows.OwnerOf(row: row)}"); } else { expected.RemoveAt(index: at); }
        }
        if (expected.Count != 0) { SdfRowChecks.DressFailure(message: $"missing {expected.Count} rebuilt rows"); }
    }

    /// <summary>Releases the row table's mesh claims.</summary>
    public void Dispose() { if (m_disposed) { return; } m_disposed = true; StartOver(); }

    // A row's draw: one piece of a group's mesh, or a copy's own mesh, the tier the group's drawn mesh was baked at, and
    // the mesh the irradiance volume traces in the placement's place with the piece the row draws.
    private sealed class DrawBinding(SdfMesh mesh, SdfMeshBakeGroup? group, int copyIndex, bool chunked = false, int tier = -1, SdfMesh? trace = null, int piece = 0) {
        private object? m_motionSource;
        private object? m_motionIdentity;
        private SdfMeshInstance? m_copy;
        private SdfMeshCopySurface? m_pickIdentity;
        private SdfMeshDraw m_draw;
        private bool m_posed;
        private Vector3 m_position;

        private Quaternion m_orientation = Quaternion.Identity;
        private float m_scale = 1f;

        private (Vector3 Position, Quaternion Orientation, float Scale) PoseOf(SdfFrame frame) {
            if ((group is not { Dynamic: true }) || (group.Slot >= frame.DynamicTransforms.Count)) { return (m_position, m_orientation, m_scale); }
            var transform = frame.DynamicTransforms[group.Slot];

            return (transform.Position, transform.Orientation, SdfMeshGeometryBuilder.UnitsOf(transform: transform));
        }

        public SdfMeshRows.Run Run;

        public int CopyIndex => copyIndex;
        public int FirstInstance => (group?.FirstInstance ?? -1);
        public int? Slot => ((group is { Dynamic: true }) ? group.Slot : null);

        public SdfMeshDraw Pose(SdfFrame frame, ref int matrices) {
            if (copyIndex >= 0) {
                var copy = frame.MeshInstances[copyIndex];
                var firstInstance = (group?.FirstInstance ?? -1);

                if (ReferenceEquals(objA: copy, objB: m_copy) && (m_draw.FrameSlot == copy.FrameSlot) && (m_pickIdentity?.FirstInstance == firstInstance)) { return m_draw; }
                m_copy = copy;
                if ((m_pickIdentity is null) || !Equals(objA: m_pickIdentity.Identity, objB: copy.Identity) || (m_pickIdentity.FirstInstance != firstInstance)) {
                    m_pickIdentity = new SdfMeshCopySurface(copy.Identity, firstInstance);
                }
                var source = (copy.MotionIdentity ?? copy.Identity);

                if (!Equals(objA: m_motionSource, objB: source)) {
                    m_motionSource = source;
                    m_motionIdentity = ((source, (((object?)group?.Selector) ?? "mesh")));
                }
                matrices++;
                return m_draw = new SdfMeshDraw(Identity: m_pickIdentity, Material: ((group is null) ? copy.Material : 0),
                    Mesh: ((group is null) ? copy.Mesh! : mesh), ObjectToWorld: copy.Matrix) {
                    FrameSlot = copy.FrameSlot,
                    Glass = (group?.Glass ?? false),
                    IsDynamic = copy.IsDynamic,
                    MotionIdentity = m_motionIdentity,
                    Piece = piece,
                    Tier = ((group is null) ? -1 : tier),
                    Trace = trace,
                    UsesRootMaterialFrame = copy.UsesRootMaterialFrame,
                };
            }
            var (position, orientation, scale) = PoseOf(frame: frame);
            if (m_posed && (m_position == position) && (m_orientation == orientation) && (m_scale == scale) && (m_draw.FrameSlot == (group!.Dynamic ? group.Slot : null)) && (m_draw.Glass == group.Glass) && (m_draw.Displacement == group.Displacement)) { return m_draw; }
            m_position = position;
            m_orientation = orientation;
            m_scale = scale;
            m_posed = true;
            if (group!.Dynamic) { matrices++; }
            var matrix = (group.Dynamic
                ? ((Matrix4x4.CreateScale(scale: scale) * Matrix4x4.CreateFromQuaternion(quaternion: orientation)) * Matrix4x4.CreateTranslation(position: position))
                : Matrix4x4.Identity);

            return m_draw = new SdfMeshDraw(Identity: (chunked ? mesh : group), Material: 0, Mesh: mesh,
                ObjectToWorld: matrix) {
                FrameSlot = (group.Dynamic ? group.Slot : null),
                Glass = group.Glass,
                // A displaced surface is never static (SdfMeshDraw.IsStatic), whatever its placement does.
                Displacement = group.Displacement,
                IsDynamic = group.Dynamic,
                Piece = piece,
                Tier = tier,
                Trace = trace,
            };
        }
    }

}


