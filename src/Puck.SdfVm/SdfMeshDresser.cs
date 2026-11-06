using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Retains the draws, poses, clipping and picking of one bake, independently of its mesh derivation.</summary>
/// <param name="bake">The bake whose landed products are dressed. Dispose the dresser before retiring the bake.</param>
public sealed class SdfMeshDresser(SdfMeshPathBake bake) : IDisposable {
    private readonly SdfMeshPathBake m_bake = bake ?? throw new ArgumentNullException(nameof(bake));
    private bool m_disposed;
    private SdfMeshClaims? m_drawClaims;
    private readonly List<SdfMeshDraw> m_draws = [];
    private IReadOnlyList<SdfMeshDraw>? m_frameDraws;
    private long m_frameDrawsRevision = -1;
    private SdfMeshInstance[] m_drawnCopies = [];
    private int[] m_drawnCopyFrames = [];
    private long m_revision;
    private long m_productRevision = -1;
    private long m_copyLayoutRevision = -1;
    private int m_drawnMeshes = -1;
    private readonly List<DrawBinding> m_drawBindings = [];
    // Each finished group the topology was built from, with its mesh, in the bake's group order: a frame whose finished
    // groups and meshes match it in order keeps the topology, read without hashing a group a frame.
    private readonly List<(SdfMeshBakeGroup Group, SdfMesh Mesh)> m_dressedGroups = [];
    private int m_hostDrawCount;

    // Reverse membership belongs to retained draw topology, not a second instance index. A changed source slot/copy
    // names only its dependent bindings; bounded run coarsening may conservatively include unchanged bindings.
    private int[] m_slotHeads = [];
    private int[] m_copyHeads = [];
    private int[] m_nextBindings = [];
    private readonly GpuUploadRuns m_bindingCandidates = new(capacity: GpuRegion.MaxCopyRuns);
    // The binding indices a frame's changed slots and copies name, gathered before AddBindingKeys sorts them in.
    private readonly List<int> m_bindingKeys = [];
    private readonly GpuUploadRuns m_hostCandidates = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_changedDraws = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_changedSlots = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuChangedRows m_drawChanges = new(history: SdfMovedTransforms.History, capacity: GpuRegion.MaxCopyRuns, trackReseats: false);
    private SdfMovedTransforms? m_movedSource;
    private IReadOnlyList<DynamicTransform>? m_transformSource;
    private long m_movedSerial;
    private GpuChangedRows? m_hostChanges;
    private long m_hostSerial;
    private SdfProgram? m_bindingProgram;
    private bool m_hadClips;
    // A failed attempt may already have rewritten retained records; retry from canonical inputs with full provenance.
    private bool m_publicationPending;

    /// <summary>Gets logical retained array payload bytes of reverse binding indexes, copy snapshots and changed-run
    /// scratch. Excludes the output journal, draw/pick-map storage and object/array headers; this is not a physical allocation total.</summary>
    public long ScratchBytes => (((long)m_slotHeads.Length + m_copyHeads.Length + m_nextBindings.Length + m_drawnCopyFrames.Length) * sizeof(int)) +
        ((long)m_drawnCopies.Length * IntPtr.Size) + m_bindingCandidates.StorageBytes + ((long)m_bindingKeys.Capacity * sizeof(int)) + m_hostCandidates.StorageBytes +
        m_changedDraws.StorageBytes + m_changedSlots.StorageBytes;

    /// <summary>Returns a frame that draws the bake's meshes beside its own mesh draws, restarting the bake when the frame
    /// carries a new program. A dynamic group draws at its slot's transform in the frame.</summary>
    /// <param name="frame">The captured frame.</param>
    /// <returns>The frame with the baked draws appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public SdfFrame Dress(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ObjectDisposedException.ThrowIf(m_disposed, this);
        using (var access = m_bake.PrepareFrame(frame)) {
            var retry = m_publicationPending;
            m_publicationPending = true;
            var topologyChanged = (retry || (m_hostDrawCount != frame.MeshDraws.Count) || (m_copyLayoutRevision != access.CopyLayoutRevision) || (m_productRevision != access.Revision));
            var programChanged = !ReferenceEquals(frame.Program, m_bindingProgram);
            var portalPath = (m_hadClips || (frame.SlotClips.Count > 0));
            var finished = 0;
            var groups = access.Groups;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++) {
                var group = groups[groupIndex];
                if (access.MeshOf(group) is not { } mesh) { continue; }
                topologyChanged |= ((finished >= m_dressedGroups.Count) || !ReferenceEquals(m_dressedGroups[finished].Group, group) ||
                    !ReferenceEquals(m_dressedGroups[finished].Mesh, mesh));
                finished++;
            }
            topologyChanged |= (finished != m_drawnMeshes);
            m_changedDraws.Clear();
            m_bindingCandidates.Clear();
            var visited = 0;
            var matrices = 0;
            var updated = 0;
            var meshesChanged = topologyChanged;
            var identitiesChanged = topologyChanged;

            if (topologyChanged) {
                BuildDrawTopology(frame, access, ref visited, ref matrices);
            } else {
                // A kept group can be interpreted under another slot after a new program. Refresh reverse slot
                // membership and visit all bindings without rebuilding geometry or assuming the moved rows reseated it.
                if (programChanged || (m_slotHeads.Length != frame.DynamicTransforms.Count)) { IndexBindings(frame); }
                if (programChanged || portalPath) {
                    m_bindingCandidates.Add(start: 0, length: m_drawBindings.Count);
                } else {
                    CollectSlotBindings(frame);
                    if (m_drawnCopies.Length != frame.MeshInstances.Count) {
                        m_bindingCandidates.Add(start: 0, length: m_drawBindings.Count);
                    } else {
                        for (var index = 0; index < m_drawnCopies.Length; index++) {
                            if ((m_drawnCopies[index] != frame.MeshInstances[index]) || (m_drawnCopyFrames[index] != frame.MeshInstances[index].FrameSlot)) {
                                AddBindings(m_copyHeads, index);
                            }
                        }
                    }
                    AddBindingKeys();
                }
                CollectHostDraws(frame, everything: portalPath);
                // Restore unclipped base records before deriving carried copies. Active/removed clips deliberately
                // use the complete base path until clipping itself supplies exact provenance.
                var baseCount = (m_hostDrawCount + m_drawBindings.Count);
                if (m_draws.Count > baseCount) { m_draws.RemoveRange(baseCount, m_draws.Count - baseCount); }
                for (var run = 0; run < m_hostCandidates.Count; run++) {
                    var end = (m_hostCandidates.Start(run) + m_hostCandidates.Length(run));
                    for (var index = m_hostCandidates.Start(run); index < end; index++) {
                        var draw = frame.MeshDraws[index];
                        var previous = m_draws[index];
                        if (previous.SameStagedContent(in draw)) { continue; }
                        meshesChanged |= !ReferenceEquals(previous.Mesh, draw.Mesh);
                        identitiesChanged |= !Equals(previous.Identity, draw.Identity);
                        m_draws[index] = draw;
                        m_changedDraws.Add(start: index, length: 1);
                    }
                }
                for (var run = 0; run < m_bindingCandidates.Count; run++) {
                    var end = (m_bindingCandidates.Start(run) + m_bindingCandidates.Length(run));
                    for (var index = m_bindingCandidates.Start(run); index < end; index++) {
                        var destination = (m_hostDrawCount + index);
                        visited++;
                        var draw = m_drawBindings[index].Pose(frame, ref matrices);
                        var previous = m_draws[destination];
                        if (previous.SameStagedContent(in draw)) { continue; }
                        meshesChanged |= !ReferenceEquals(previous.Mesh, draw.Mesh);
                        identitiesChanged |= !Equals(previous.Identity, draw.Identity);
                        m_draws[destination] = draw;
                        m_changedDraws.Add(start: destination, length: 1);
                        updated++;
                    }
                }
            }

            var changed = (topologyChanged || portalPath || (m_changedDraws.Count > 0));
            if (changed) {
                SdfSlotClip.Apply(clips: frame.SlotClips, draws: m_draws);
                if (meshesChanged) { (m_drawClaims ??= new SdfMeshClaims()).Update(draws: m_draws); }
            }
            if (!changed && ReferenceEquals(frame, m_dressedSource) && (m_dressed is { } held)) {
                CountWork(visited, matrices, updated);
                CommitInputs(frame);
                m_publicationPending = false;
                access.Acknowledge();
                return held;
            }

            var dressedMap = m_dressedMap;
            var spareMap = m_spareMap;
            if (frame.PickMap is { } hostMap) {
                // Exact changed identities are known on the ordinary path. Motion/material changes cannot invalidate
                // a captured pick map; clipping/topology keep the conservative complete correspondence check.
                var retainsMap = (!identitiesChanged && !portalPath && (dressedMap is not null) &&
                    ReferenceEquals(dressedMap.Inner, hostMap) && (dressedMap.HostDraws == frame.MeshDraws.Count));
                if (!retainsMap && !Dresses(dressedMap, hostMap, frame.MeshDraws.Count)) {
                    var previous = dressedMap;
                    dressedMap = (Dresses(spareMap, hostMap, frame.MeshDraws.Count)
                        ? spareMap
                        : new SdfDressedPickMap(chunkOwners: m_chunkOwners, draws: m_draws, hostDraws: frame.MeshDraws.Count, inner: hostMap));
                    spareMap = previous;
                }
            } else { dressedMap = null; spareMap = null; }

            var revision = (changed ? checked(m_revision + 1L) : m_revision);
            // Construct the output before committing either journal or input cursor. The serial captured in the
            // frame never follows this mutable producer when a later frame is dressed or a consumer skips frames.
            var serial = checked(m_drawChanges.Serial + 1L);
            var dressed = (frame with {
                MeshDraws = m_draws, MeshDrawsRevision = revision, PickMap = dressedMap,
                MeshDrawChanges = m_drawChanges, MeshDrawChangesSerial = serial,
            });
            m_drawChanges.Begin(everything: (topologyChanged || portalPath), rows: m_draws.Count);
            for (var run = 0; run < m_changedDraws.Count; run++) {
                m_drawChanges.Owe(start: m_changedDraws.Start(run), count: m_changedDraws.Length(run));
            }
            m_dressedMap = dressedMap;
            m_spareMap = spareMap;
            m_dressed = dressed;
            m_dressedSource = frame;
            m_drawnMeshes = finished;
            m_revision = revision;
            m_productRevision = access.Revision;
            m_copyLayoutRevision = access.CopyLayoutRevision;
            CommitInputs(frame);
            CountWork(visited, matrices, updated);
            m_publicationPending = false;
            access.Acknowledge();
            return dressed;
        }
    }

    private void CountWork(int visited, int matrices, int updated) {
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressPoseCandidates, amount: m_drawBindings.Count);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressBindingsVisited, amount: visited);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressMatricesComputed, amount: matrices);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressedRecordsUpdated, amount: updated);
    }

    private void CommitInputs(SdfFrame frame) {
        if (m_drawnCopies.Length != frame.MeshInstances.Count) { m_drawnCopies = new SdfMeshInstance[frame.MeshInstances.Count]; m_drawnCopyFrames = new int[frame.MeshInstances.Count]; }
        for (var index = 0; index < m_drawnCopies.Length; index++) { m_drawnCopies[index] = frame.MeshInstances[index]; m_drawnCopyFrames[index] = frame.MeshInstances[index].FrameSlot; }
        m_frameDraws = frame.MeshDraws;
        m_frameDrawsRevision = frame.MeshDrawsRevision;
        m_movedSource = frame.MovedTransforms;
        m_movedSerial = frame.MovedTransformsSerial;
        m_transformSource = frame.DynamicTransforms;
        m_hostChanges = frame.MeshDrawChanges;
        m_hostSerial = frame.MeshDrawChangesSerial;
        m_bindingProgram = frame.Program;
        m_hadClips = (frame.SlotClips.Count > 0);
    }

    private void CollectSlotBindings(SdfFrame frame) {
        m_changedSlots.Clear();
        var moved = frame.MovedTransforms;
        if ((moved is null) || !ReferenceEquals(moved, m_movedSource) || !ReferenceEquals(frame.DynamicTransforms, m_transformSource) ||
            !moved.TryCollect(since: m_movedSerial, through: frame.MovedTransformsSerial, into: m_changedSlots)) {
            for (var slot = 0; slot < m_slotHeads.Length; slot++) { AddBindings(m_slotHeads, slot); }
            return;
        }
        for (var run = 0; run < m_changedSlots.Count; run++) {
            var end = (m_changedSlots.Start(run) + m_changedSlots.Length(run));
            for (var slot = m_changedSlots.Start(run); slot < end; slot++) { AddBindings(m_slotHeads, slot); }
        }
    }

    private void CollectHostDraws(SdfFrame frame, bool everything) {
        m_hostCandidates.Clear();
        if (frame.MeshDraws.Count == 0) { return; }
        var sourceChanged = (!ReferenceEquals(frame.MeshDraws, m_frameDraws) || (frame.MeshDrawsRevision != m_frameDrawsRevision));
        var feed = frame.MeshDrawChanges;
        if (!everything && !sourceChanged && ReferenceEquals(feed, m_hostChanges) && (frame.MeshDrawChangesSerial == m_hostSerial)) { return; }
        if (!everything && (feed is not null) && ReferenceEquals(feed, m_hostChanges) &&
            (!sourceChanged || (frame.MeshDrawChangesSerial > m_hostSerial)) &&
            feed.TryCollect(since: m_hostSerial, through: frame.MeshDrawChangesSerial, into: m_hostCandidates) &&
            FitsRows(m_hostCandidates, frame.MeshDraws.Count)) { return; }
        m_hostCandidates.Clear();
        m_hostCandidates.Add(start: 0, length: frame.MeshDraws.Count);
    }

    private static bool FitsRows(GpuUploadRuns runs, int rows) {
        for (var run = 0; run < runs.Count; run++) {
            if ((runs.Start(run) < 0) || (runs.Length(run) > (rows - runs.Start(run)))) { return false; }
        }
        return true;
    }

    private void AddBindings(int[] heads, int key) {
        if ((uint)key >= (uint)heads.Length) { return; }
        for (var index = heads[key]; index >= 0; index = m_nextBindings[index]) {
            m_bindingKeys.Add(item: index);
        }
    }
    // Hands the collected binding indices to the candidate runs in ascending order. Slots and copies name their bindings
    // out of binding order, and a run set takes an out-of-order index by shifting its runs.
    private void AddBindingKeys() {
        var keys = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_bindingKeys);

        keys.Sort();
        foreach (var key in keys) {
            m_bindingCandidates.Add(start: key, length: 1);
        }
        m_bindingKeys.Clear();
    }

    private void IndexBindings(SdfFrame frame) {
        if (m_slotHeads.Length != frame.DynamicTransforms.Count) { m_slotHeads = new int[frame.DynamicTransforms.Count]; }
        if (m_copyHeads.Length != frame.MeshInstances.Count) { m_copyHeads = new int[frame.MeshInstances.Count]; }
        if (m_nextBindings.Length < m_drawBindings.Count) { m_nextBindings = new int[Math.Max(m_drawBindings.Count, 2 * m_nextBindings.Length)]; }
        Array.Fill(m_slotHeads, -1);
        Array.Fill(m_copyHeads, -1);
        Array.Fill(m_nextBindings, -1, 0, m_drawBindings.Count);
        // A binding belongs to exactly one copy or one dynamic slot. Prepending in reverse draw order keeps each
        // adjacency chain ascending without allocating a list/object per slot or duplicating its membership.
        for (var index = m_drawBindings.Count - 1; index >= 0; index--) {
            var binding = m_drawBindings[index];
            if ((uint)binding.CopyIndex < (uint)m_copyHeads.Length) {
                m_nextBindings[index] = m_copyHeads[binding.CopyIndex];
                m_copyHeads[binding.CopyIndex] = index;
            } else if ((binding.CopyIndex < 0) && (binding.Slot is { } slot) && ((uint)slot < (uint)m_slotHeads.Length)) {
                m_nextBindings[index] = m_slotHeads[slot];
                m_slotHeads[slot] = index;
            }
        }
    }

    private void BuildDrawTopology(SdfFrame frame, SdfMeshPathBake.FrameAccess access, ref int visited, ref int matrices) {
        SdfMeshHistory.ProcessWork.Count(SdfMeshHistory.DressTopologyBuilds);
        m_draws.Clear();
        m_chunkOwners.Clear();
        m_drawBindings.Clear();
        m_dressedGroups.Clear();
        m_hostDrawCount = frame.MeshDraws.Count;
        m_draws.AddRange(frame.MeshDraws);
        foreach (var group in access.Groups) {
            if (access.MeshOf(group) is not { } mesh) { continue; }
            m_dressedGroups.Add((group, mesh));
            if (access.CopiesOf(group) is { } copies) {
                foreach (var copy in copies) {
                    for (var piece = 0; piece < Math.Max(1, mesh.Chunks.Count); piece++) {
                        Bind(new DrawBinding(((mesh.Chunks.Count == 0) ? mesh : mesh.Chunks[piece]), group, copy));
                    }
                }
                continue;
            }
            for (var piece = 0; piece < Math.Max(1, mesh.Chunks.Count); piece++) {
                var part = ((mesh.Chunks.Count == 0) ? mesh : mesh.Chunks[piece]);
                Bind(new DrawBinding(part, group, -1, chunked: mesh.Chunks.Count > 0));
                if (mesh.Chunks.Count > 0) { m_chunkOwners[part] = group; }
            }
        }
        for (var index = 0; index < frame.MeshInstances.Count; index++) {
            if (frame.MeshInstances[index].Mesh is { } mesh) { Bind(new DrawBinding(mesh, null, index)); }
        }
        IndexBindings(frame);
        for (var index = 0; index < m_drawBindings.Count; index++) {
            visited++;
            m_draws.Add(m_drawBindings[index].Pose(frame, ref matrices));
        }
        void Bind(DrawBinding binding) => m_drawBindings.Add(binding);
    }

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
            return (transform.Position, transform.Orientation, SdfMeshGeometryBuilder.UnitsOf(transform));
        }
        public int CopyIndex => copyIndex;
        public int? Slot => ((group is { Dynamic: true }) ? group.Slot : null);

        public SdfMeshDraw Pose(SdfFrame frame, ref int matrices) {
            if (copyIndex >= 0) {
                var copy = frame.MeshInstances[copyIndex];
                var firstInstance = (group?.FirstInstance ?? -1);
                if (ReferenceEquals(copy, m_copy) && (m_draw.FrameSlot == copy.FrameSlot) && (m_pickIdentity?.FirstInstance == firstInstance)) { return m_draw; }
                m_copy = copy;
                if ((m_pickIdentity is null) || !Equals(m_pickIdentity.Identity, copy.Identity) || (m_pickIdentity.FirstInstance != firstInstance)) {
                    m_pickIdentity = new SdfMeshCopySurface(copy.Identity, firstInstance);
                }
                var source = (copy.MotionIdentity ?? copy.Identity);
                if (!Equals(m_motionSource, source)) {
                    m_motionSource = source;
                    m_motionIdentity = ((source, (object?)group?.Selector ?? "mesh"));
                }
                matrices++;
                return m_draw = new SdfMeshDraw(Identity: m_pickIdentity, Material: ((group is null) ? copy.Material : 0),
                    Mesh: ((group is null) ? copy.Mesh! : mesh), ObjectToWorld: copy.Matrix) {
                    FrameSlot = copy.FrameSlot, UsesRootMaterialFrame = copy.UsesRootMaterialFrame, Glass = (group?.Glass ?? false), IsDynamic = copy.IsDynamic,
                    MotionIdentity = m_motionIdentity,
                };
            }
            var (position, orientation, scale) = PoseOf(frame);
            if (m_posed && (m_position == position) && (m_orientation == orientation) && (m_scale == scale) && (m_draw.FrameSlot == (group!.Dynamic ? group.Slot : null)) && (m_draw.Glass == group.Glass) && (m_draw.Displacement == group.Displacement)) { return m_draw; }
            m_position = position;
            m_orientation = orientation;
            m_scale = scale;
            m_posed = true;
            if (group!.Dynamic) { matrices++; }
            var matrix = (group.Dynamic
                ? ((Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(orientation)) * Matrix4x4.CreateTranslation(position))
                : Matrix4x4.Identity);
            return m_draw = new SdfMeshDraw(Identity: (chunked ? mesh : group), Material: 0, Mesh: mesh,
                ObjectToWorld: matrix) {
                FrameSlot = (group.Dynamic ? group.Slot : null), Glass = group.Glass, Displacement = group.Displacement,
                // A displaced surface moves as it is drawn, so it stays out of the irradiance volume's static scene.
                IsDynamic = (group.Dynamic || (group.Displacement is not null)),
            };
        }
    }

    // The frame Dress last dressed and what it returned, which a still frame source capturing the same frame takes again
    // without a new frame.
    private SdfFrame? m_dressedSource;
    private SdfFrame? m_dressed;
    private SdfDressedPickMap? m_dressedMap;

    // The group each chunk of a chunked group's mesh is drawn for, as the last dressing listed them.
    private readonly Dictionary<object, SdfMeshBakeGroup> m_chunkOwners = new(comparer: ReferenceEqualityComparer.Instance);

    private SdfDressedPickMap? m_spareMap;

    // Whether a pick map extends a host map over exactly the dressed draws.
    private bool Dresses([NotNullWhen(returnValue: true)] SdfDressedPickMap? map, ISdfPickMap hostMap, int hostDraws) => (
        (map is not null) &&
        ReferenceEquals(objA: map.Inner, objB: hostMap) &&
        (map.HostDraws == hostDraws) &&
        map.Matches(draws: m_draws)
    );

    /// <summary>Releases the dresser's retained mesh claims. The bake and its library remain caller-owned.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        m_drawClaims?.Dispose();
        m_drawClaims = null;
        m_draws.Clear();
        m_drawBindings.Clear();
        m_slotHeads = [];
        m_copyHeads = [];
        m_nextBindings = [];
        m_dressedGroups.Clear();
        m_chunkOwners.Clear();
        m_drawnCopies = [];
        m_drawnCopyFrames = [];
        m_movedSource = null;
        m_transformSource = null;
        m_hostChanges = null;
        m_frameDraws = null;
        m_bindingProgram = null;
        m_dressed = null;
        m_dressedSource = null;
        m_dressedMap = null;
        m_spareMap = null;
    }
}
