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
    private readonly GpuRowCursor m_slotCursor = new();
    private readonly GpuRowCursor m_hostCursor = new();
    private SdfMeshInstance[] m_copies = [];
    private int[] m_copyFrames = [];

    private SdfProgram? m_program;

    private long m_layout = -1;

    private SdfMeshRows.Run m_host;
    private SdfMeshRows.Run m_clipCopies;

    private readonly List<SdfMeshDraw> m_clipScratch = [];
    private readonly List<(SdfMeshRows.Run Run, int Offset)> m_clipSources = [];
    private readonly List<int> m_clipCopiedIndices = [];
    private readonly List<(int Row, ulong Occupancy)> m_clipOccupants = [];

    private bool m_hadClips;
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
    public long ScratchBytes => (((((m_bindingsByOwner.StorageBytes + m_candidates.StorageBytes) + m_slots.StorageBytes) + m_hostChanges.StorageBytes) + (((long)m_bindings.Length) * IntPtr.Size)) + (((long)m_copies.Length) * (IntPtr.Size + sizeof(int))));

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
        var programChanged = !ReferenceEquals(objA: m_program, objB: frame.Program);
        var portal = (m_hadClips || (frame.SlotClips.Count > 0));
        var hostNew = (m_host.Count != frame.MeshDraws.Count);
        var hostWhole = m_hostCursor.Collect(frame.MeshDrawChanges, frame.MeshDrawChangesSerial, frame.MeshDraws,
            frame.MeshDraws.Count, frame.MeshDrawsRevision, m_hostChanges, force: (startOver || hostNew));

        if (hostWhole) { SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.HostWhole); }
        _ = m_slotCursor.Collect(frame.MovedTransforms?.Changes, frame.MovedTransformsSerial, frame.DynamicTransforms,
            frame.DynamicTransforms.Count, 0, m_slots, force: (startOver || programChanged));
        var matrices = 0;
        var visited = 0;
        var updated = 0;

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
        foreach (var state in m_groups.Values) { state.Seen = false; }
        for (var g = 0; (g < access.GroupCount); g++) {
            var group = access.GroupAt(index: g);

            if (access.MeshOf(group: group) is not { } mesh) { continue; }
            if (!m_groups.TryGetValue(key: group, value: out var state)) { m_groups.Add(key: group, value: state = new()); }
            state.Seen = true;
            var copies = access.CopiesOf(group: group);
            var count = (mesh.PieceCount * (copies?.Count ?? 1));
            var changed = (!ReferenceEquals(objA: mesh, objB: state.Mesh) || (state.Run.Count != count) ||
                ((m_layout != access.CopyLayoutRevision) && !state.Copies.SequenceEqual(second: (copies ?? Array.Empty<int>()))));

            if (!changed) { continue; }
            var old = state.Run;

            if (old.Count != count) { state.Run = m_writer.Allocate(count: count); }
            RemoveBindings(run: old);
            state.Mesh = mesh;
            state.Copies = (copies?.ToArray() ?? []);

            for (var offset = 0; (offset < count); offset++) {
                var binding = new DrawBinding(mesh.Piece(index: (offset % mesh.PieceCount)), group,
                    ((copies is null) ? -1 : copies[(offset / mesh.PieceCount)]), ((copies is null) && (mesh.Chunks.Count > 0)));

                Bind(binding: binding, frame: frame, matrices: ref matrices, offset: offset, run: state.Run);
                visited++; updated++;
            }
            if (old != state.Run) { m_writer.Free(run: old); }
        }
        m_removedGroups.Clear();
        foreach (var (group, state) in m_groups) { if (!state.Seen) { RemoveBindings(run: state.Run); m_writer.Free(run: state.Run); m_removedGroups.Add(item: group); } }
        foreach (var group in m_removedGroups) { _ = m_groups.Remove(key: group); }
        foreach (var state in m_own.Values) { state.Seen = false; }
        for (var i = 0; (i < frame.MeshInstances.Count); i++) {
            if (frame.MeshInstances[i].Mesh is not { } mesh) { continue; }
            if (!m_own.TryGetValue(key: i, value: out var state)) { m_own.Add(key: i, value: state = new()); }
            state.Seen = true;
            if (ReferenceEquals(objA: mesh, objB: state.Mesh)) { continue; }
            if (state.Run.Count == 0) { state.Run = m_writer.Allocate(count: 1); }
            RemoveBindings(run: state.Run);
            state.Mesh = mesh;
            Bind(state.Run, 0, new DrawBinding(mesh, null, i), frame, ref matrices);
            visited++; updated++;
        }
        m_removedOwn.Clear();
        foreach (var (i, state) in m_own) { if (!state.Seen) { RemoveBindings(run: state.Run); m_writer.Free(run: state.Run); m_removedOwn.Add(item: i); } }
        foreach (var i in m_removedOwn) { _ = m_own.Remove(key: i); }
        m_candidates.Clear();
        if (programChanged) {
            for (var row = 0; (row < m_bindings.Length); row++) { if (m_bindings[row] is not null) { m_candidates.Add(length: 1, start: row); } }
        }
        for (var run = 0; (run < m_slots.Count); run++) {
            for (var slot = m_slots.Start(index: run); (slot < (m_slots.Start(index: run) + m_slots.Length(index: run))); slot++) { AddCandidates(key: (false, slot)); }
        }
        for (var i = 0; (i < frame.MeshInstances.Count); i++) {
            if (programChanged || (i >= m_copies.Length) || !ReferenceEquals(objA: m_copies[i], objB: frame.MeshInstances[i]) || (m_copyFrames[i] != frame.MeshInstances[i].FrameSlot)) { AddCandidates(key: (true, i)); }
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
        if (portal) { ApplyClips(frame: frame); }
        // Clip-copy membership is derived by the conservative portal walk in R1b. If that walk allocated or freed
        // rows on an otherwise canonical locked frame, reconcile once from empty before publishing it.
        if (frame.TickLocked && !startOver && (m_program is not null) && !m_rows.Canonical) {
            m_pending = false;
            return Dress(frame: frame);
        }
        if (SdfRowChecks.Enabled) { CheckDress(access: access, frame: frame); }
        m_writer.Publish(cause: (startOver ? (retry ? SdfMeshStagingCause.Retry : SdfMeshStagingCause.Rebuild) : (portal ? SdfMeshStagingCause.Portal : null)));
        if ((m_map is null) || !ReferenceEquals(objA: m_map.Inner, objB: frame.HostPickMap)) { m_map = ((frame.HostPickMap is { } inner) ? new SdfDressedPickMap(inner: inner, rows: m_rows) : null); }
        // The frame answers with this dresser's rows and map by reference, so a frame that stands is the frame answered
        // and no copy is made. A frame another dresser dressed is copied first: its rows are theirs to keep.
        var result = (((frame.MeshRows is null) || ReferenceEquals(objA: frame.MeshRows, objB: m_rows)) ? frame : (frame with { }));

        result.AdoptDressing(rows: m_rows, picks: m_map);

        if (m_copies.Length != frame.MeshInstances.Count) { m_copies = new SdfMeshInstance[frame.MeshInstances.Count]; m_copyFrames = new int[m_copies.Length]; }
        for (var i = 0; (i < m_copies.Length); i++) { m_copies[i] = frame.MeshInstances[i]; m_copyFrames[i] = m_copies[i].FrameSlot; }
        if (SdfRowChecks.SkipHostRequested) { m_skipHostThrough = ((m_rows.Serial + SdfMovedTransforms.History) + 2); SdfRowChecks.SkipHostRequested = false; }
        if (m_rows.Serial <= m_skipHostThrough) { SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.HostSkipped); } else { m_hostCursor.Commit(); }
        m_slotCursor.Commit();
        m_program = frame.Program;
        m_layout = access.CopyLayoutRevision;
        m_hadClips = (frame.SlotClips.Count > 0);
        m_pending = false;
        access.Acknowledge();

        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressBindingsVisited, visited);
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressMatricesComputed, matrices);
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.DressedRecordsUpdated, updated);
        return result;
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
        m_host = default; m_clipCopies = default; m_program = null; m_layout = -1; m_clipOccupants.Clear();
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
    private static SdfMeshRows.Owner OwnerOf(DrawBinding binding) => new(Instance: binding.FirstInstance);
    private void IndexBinding(int row, DrawBinding binding) {
        m_bindingsByOwner.Remove(row: row);
        if (binding.CopyIndex >= 0) { m_bindingsByOwner.Set(row, (true, binding.CopyIndex)); } else if (binding.Slot is { } slot) { m_bindingsByOwner.Set(key: (false, slot), row: row); }
    }
    private void AddCandidates((bool Copy, int Index) key) {
        for (var row = m_bindingsByOwner.First(key: key); (row >= 0); row = m_bindingsByOwner.Next(row: row)) { m_candidates.Add(length: 1, start: row); }
    }
    private (SdfMeshRows.Run Run, int Offset) BindingRun(int row) => (m_bindings[row]!.Run, (row - m_bindings[row]!.Run.Start));
    private void ApplyClips(SdfFrame frame) {
        m_clipScratch.Clear(); m_clipSources.Clear(); m_clipCopiedIndices.Clear();
        AddBase(run: m_host);
        foreach (var state in m_groups.Values) { AddBase(run: state.Run); }
        foreach (var state in m_own.Values) { AddBase(run: state.Run); }
        var count = m_clipScratch.Count;

        SdfSlotClip.Apply(m_clipScratch, frame.SlotClips, m_clipCopiedIndices);
        var copyCount = (m_clipScratch.Count - count);
        var copiesChanged = (m_clipCopies.Count != copyCount);

        for (var i = 0; (!copiesChanged && (i < copyCount)); i++) {
            var (run, offset) = m_clipSources[m_clipCopiedIndices[i]];
            copiesChanged = (m_clipOccupants[i] != ((run.Start + offset), m_rows.Occupancy(row: (run.Start + offset))));
        }
        if (copiesChanged) { var old = m_clipCopies; m_clipCopies = m_writer.Allocate(count: copyCount); m_writer.Free(run: old); }
        for (var i = 0; (i < count); i++) {
            var (run, offset) = m_clipSources[i];
            m_writer.SetStaged(run, offset, m_clipScratch[i], m_rows.OwnerOf(row: (run.Start + offset)));
        }
        m_clipOccupants.Clear();
        for (var copied = 0; (copied < copyCount); copied++) {
            var (run, offset) = m_clipSources[m_clipCopiedIndices[copied]];
            var source = (run.Start + offset);

            m_writer.Set(m_clipCopies, copied, m_clipScratch[(count + copied)], new(CopiedRow: source));
            m_clipOccupants.Add(item: (source, m_rows.Occupancy(row: source)));
        }
    }
    private void AddBase(SdfMeshRows.Run run) {
        for (var i = 0; (i < run.Count); i++) { m_clipScratch.Add(item: m_rows.BaseAt(row: (run.Start + i))); m_clipSources.Add(item: (run, i)); }
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.RowsWalkedWhole, run.Count);
    }
    private void CheckDress(SdfFrame frame, SdfMeshPathBake.FrameAccess access) {
        var expected = new List<SdfMeshDraw>(collection: frame.MeshDraws);
        var matrices = 0;

        for (var g = 0; (g < access.GroupCount); g++) {
            var group = access.GroupAt(index: g);

            if (access.MeshOf(group: group) is not { } mesh) { continue; }
            var copies = access.CopiesOf(group: group);

            for (var c = 0; (c < (copies?.Count ?? 1)); c++) {
                for (var p = 0; (p < mesh.PieceCount); p++) { expected.Add(item: new DrawBinding(mesh.Piece(index: p), group, ((copies is null) ? -1 : copies[c]), ((copies is null) && (mesh.Chunks.Count > 0))).Pose(frame: frame, matrices: ref matrices)); }
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

    private sealed class DrawBinding(SdfMesh mesh, SdfMeshBakeGroup? group, int copyIndex, bool chunked = false) {
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
                Displacement = group.Displacement,
                // A displaced surface moves as it is drawn, so it stays out of the irradiance volume's static scene.
                IsDynamic = (group.Dynamic || (group.Displacement is not null)),
            };
        }
    }

}


