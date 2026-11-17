using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Retains the draws, poses, clipping and picking of one bake, independently of its mesh derivation.</summary>
/// <param name="bake">The bake whose landed products are dressed. Dispose the dresser before retiring the bake.</param>
public sealed class SdfMeshDresser(SdfMeshPathBake bake) : IDisposable {
    private readonly SdfMeshPathBake m_bake = (bake ?? throw new ArgumentNullException(paramName: nameof(bake)));

    private bool m_disposed;
    private SdfMeshClaims? m_drawClaims;

    private readonly List<SdfMeshDraw> m_draws = [];
    private SdfMeshInstance[] m_drawnCopies = [];
    private int[] m_drawnCopyFrames = [];

    private long m_revision;

    private long m_copyLayoutRevision = -1;
    private int m_drawnMeshes = -1;
    private readonly List<DrawBinding> m_drawBindings = [];
    // A topology rebuild's previous bindings by what each draws, so a binding the new topology names again keeps its
    // object and its pose caches, which compare against the frame as on a frame that keeps the topology.
    private readonly Dictionary<DrawBinding.Key, DrawBinding> m_reusableBindings = new(comparer: DrawBinding.KeyComparer.Instance);
    // Each finished group the topology was built from, with its mesh, its run of bindings and its copies (-1 for a
    // group drawn in place), in the bake's group order: a frame whose finished groups and meshes match it in order
    // keeps the topology, read without hashing a group a frame.
    private readonly List<(SdfMeshBakeGroup Group, SdfMesh Mesh, int Start, int Count, int Copies)> m_dressedGroups = [];
    // The dressed groups a frame replaced, group or mesh, with as many pieces and copies as before, by their place in
    // m_dressedGroups: each keeps its run of bindings, whose draws alone change (Replace).
    private readonly List<(int At, SdfMeshBakeGroup Group, SdfMesh Mesh)> m_replaced = [];
    // Each group's run length in the topology a rebuild replaces, which the rebuild compares its owners against.
    private readonly Dictionary<SdfMeshBakeGroup, int> m_previousRuns = new(comparer: ReferenceEqualityComparer.Instance);

    private int m_hostDrawCount;

    // Reverse membership belongs to retained draw topology, not a second instance index. A changed source slot/copy
    // names only its dependent bindings; bounded run coarsening may conservatively include unchanged bindings.
    private readonly GpuRowIndex<(bool Copy, int Index)> m_bindingsByOwner = new();

    private int m_indexedSlots;

    private readonly GpuUploadRuns m_bindingCandidates = new(capacity: GpuRegion.MaxCopyRuns);
    // The binding indices a frame's changed slots and copies name, gathered before AddBindingKeys sorts them in.
    private readonly List<int> m_bindingKeys = [];
    private readonly GpuUploadRuns m_hostCandidates = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_changedDraws = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_changedSlots = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuChangedRows m_drawChanges = new(capacity: GpuRegion.MaxCopyRuns, history: SdfMovedTransforms.History, trackReseats: false);
    private readonly GpuRowCursor m_hostCursor = new();
    private readonly GpuRowCursor m_slotCursor = new();

    private SdfProgram? m_bindingProgram;
    private bool m_hadClips;
    // A failed attempt may already have rewritten retained records; retry from canonical inputs with full provenance.
    private bool m_publicationPending;

    /// <summary>Gets logical retained array payload bytes of reverse binding indexes, copy snapshots and changed-run
    /// scratch. Excludes the output journal, draw/pick-map storage and object/array headers; this is not a physical allocation total.</summary>
    public long ScratchBytes => (((((((m_bindingsByOwner.StorageBytes + (((long)m_drawnCopyFrames.Length) * sizeof(int))) +
        (((long)m_drawnCopies.Length) * IntPtr.Size)) + m_bindingCandidates.StorageBytes) + (((long)m_bindingKeys.Capacity) * sizeof(int))) + m_hostCandidates.StorageBytes) +
        m_changedDraws.StorageBytes) + m_changedSlots.StorageBytes);

    /// <summary>Returns a frame that draws the bake's meshes beside its own mesh draws, restarting the bake when the frame
    /// carries a new program. A dynamic group draws at its slot's transform in the frame.</summary>
    /// <param name="frame">The captured frame.</param>
    /// <returns>The frame with the baked draws appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public SdfFrame Dress(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        using (var access = m_bake.PrepareFrame(frame: frame)) {
            var retry = m_publicationPending;

            m_publicationPending = true;
            // The bake's product revision is not compared: every product a frame draws is a (group, mesh) pair the
            // walk below reads, and a pair replaced with its piece and copy counts standing keeps the topology.
            // The first reason that fires names the rebuild (SdfMeshHistory.TopologyBuildsOf).
            SdfMeshTopologyReason? reason = (retry
                ? SdfMeshTopologyReason.Retry
                : ((m_hostDrawCount != frame.MeshDraws.Count)
                    ? SdfMeshTopologyReason.HostDraws
                    : ((m_copyLayoutRevision != access.CopyLayoutRevision) ? SdfMeshTopologyReason.CopyLayout : null)));
            var programChanged = !ReferenceEquals(objA: frame.Program, objB: m_bindingProgram);
            var portalPath = (m_hadClips || (frame.SlotClips.Count > 0));
            var finished = 0;
            var groups = access.Groups;

            m_replaced.Clear();
            for (var groupIndex = 0; (groupIndex < groups.Count); groupIndex++) {
                var group = groups[groupIndex];

                if (access.MeshOf(group: group) is not { } mesh) { continue; }
                if (finished >= m_dressedGroups.Count) {
                    reason ??= SdfMeshTopologyReason.GroupNew;
                } else if ((m_dressedGroups[finished] is var listed) && (!ReferenceEquals(objA: listed.Group, objB: group) || !ReferenceEquals(objA: listed.Mesh, objB: mesh))) {
                    if ((reason is null) && (mesh.PieceCount == listed.Mesh.PieceCount) && ((access.CopiesOf(group: group)?.Count ?? -1) == listed.Copies)) {
                        m_replaced.Add(item: (finished, group, mesh));
                    } else {
                        reason ??= SdfMeshTopologyReason.GroupReshaped;
                    }
                }
                finished++;
            }
            if (finished != m_drawnMeshes) { reason ??= SdfMeshTopologyReason.GroupCount; }
            var topologyChanged = (reason is not null);

            _ = m_slotCursor.Collect(frame.MovedTransforms?.Changes, frame.MovedTransformsSerial, frame.DynamicTransforms,
                frame.DynamicTransforms.Count, 0, m_changedSlots, force: (topologyChanged || programChanged || portalPath));
            _ = m_hostCursor.Collect(frame.MeshDrawChanges, frame.MeshDrawChangesSerial, frame.MeshDraws,
                frame.MeshDraws.Count, frame.MeshDrawsRevision, m_hostCandidates, force: (topologyChanged || portalPath));
            m_changedDraws.Clear();
            m_bindingCandidates.Clear();
            var visited = 0;
            var matrices = 0;
            var updated = 0;
            // The rows whose owner the frame changed, whole (SdfMeshHistory.RowsOwnedChanged).
            var owned = 0;
            var meshesChanged = topologyChanged;
            var identitiesChanged = (topologyChanged || (m_replaced.Count > 0));

            if (reason is { } rebuilt) {
                BuildDrawTopology(access: access, frame: frame, matrices: ref matrices, owned: ref owned, reason: rebuilt, visited: ref visited);
            } else {
                // A kept group can be interpreted under another slot after a new program. Refresh reverse slot
                // membership and visit all bindings without rebuilding geometry or assuming the moved rows reseated it.
                if (programChanged || (m_indexedSlots != frame.DynamicTransforms.Count)) { IndexBindings(frame: frame); }
                if (programChanged || portalPath) {
                    m_bindingCandidates.Add(start: 0, length: m_drawBindings.Count);
                } else {
                    CollectSlotBindings(frame: frame);
                    if (m_drawnCopies.Length != frame.MeshInstances.Count) {
                        m_bindingCandidates.Add(start: 0, length: m_drawBindings.Count);
                    } else {
                        for (var index = 0; (index < m_drawnCopies.Length); index++) {
                            if ((m_drawnCopies[index] != frame.MeshInstances[index]) || (m_drawnCopyFrames[index] != frame.MeshInstances[index].FrameSlot)) {
                                AddBindings(key: (true, index));
                            }
                        }
                    }
                    AddBindingKeys();
                }
                // Restore unclipped base records before deriving carried copies. Active/removed clips deliberately
                // use the complete base path until clipping itself supplies exact provenance.
                var baseCount = (m_hostDrawCount + m_drawBindings.Count);

                if (m_draws.Count > baseCount) { m_draws.RemoveRange(baseCount, (m_draws.Count - baseCount)); }
                if (m_replaced.Count > 0) { Replace(access: access, frame: frame, matrices: ref matrices, owned: ref owned, updated: ref updated, visited: ref visited); }
                for (var run = 0; (run < m_hostCandidates.Count); run++) {
                    var end = (m_hostCandidates.Start(index: run) + m_hostCandidates.Length(index: run));

                    for (var index = m_hostCandidates.Start(index: run); (index < end); index++) {
                        var draw = frame.MeshDraws[index];
                        var previous = m_draws[index];

                        if (previous.SameStagedContent(other: in draw)) { continue; }
                        if (!ReferenceEquals(objA: previous.Mesh, objB: draw.Mesh)) { meshesChanged = true; owned++; }
                        identitiesChanged |= !Equals(objA: previous.Identity, objB: draw.Identity);
                        m_draws[index] = draw;
                        m_changedDraws.Add(length: 1, start: index);
                    }
                }
                for (var run = 0; (run < m_bindingCandidates.Count); run++) {
                    var end = (m_bindingCandidates.Start(index: run) + m_bindingCandidates.Length(index: run));

                    for (var index = m_bindingCandidates.Start(index: run); (index < end); index++) {
                        var destination = (m_hostDrawCount + index);

                        visited++;
                        var draw = m_drawBindings[index].Pose(frame: frame, matrices: ref matrices);
                        var previous = m_draws[destination];

                        if (previous.SameStagedContent(other: in draw)) { continue; }
                        if (!ReferenceEquals(objA: previous.Mesh, objB: draw.Mesh)) { meshesChanged = true; owned++; }
                        identitiesChanged |= !Equals(objA: previous.Identity, objB: draw.Identity);
                        m_draws[destination] = draw;
                        m_changedDraws.Add(length: 1, start: destination);
                        updated++;
                    }
                }
            }

            var changed = (topologyChanged || portalPath || (m_changedDraws.Count > 0));

            if (changed) {
                SdfSlotClip.Apply(clips: frame.SlotClips, draws: m_draws);
                if (meshesChanged) { (m_drawClaims ??= new SdfMeshClaims()).Update(draws: m_draws); }
            }
            if (!changed && ReferenceEquals(objA: frame, objB: m_dressedSource) && (m_dressed is { } held)) {
                CountWork(matrices: matrices, owned: owned, updated: updated, visited: visited);
                CommitInputs(frame: frame);
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
                    ReferenceEquals(objA: dressedMap.Inner, objB: hostMap) && (dressedMap.HostDraws == frame.MeshDraws.Count));

                if (!retainsMap && !Dresses(dressedMap, hostMap, frame.MeshDraws.Count)) {
                    var previous = dressedMap;

                    dressedMap = (Dresses(spareMap, hostMap, frame.MeshDraws.Count)
                        ? spareMap
                        : new SdfDressedPickMap(chunkOwners: m_chunkOwners, draws: m_draws, hostDraws: frame.MeshDraws.Count, inner: hostMap));
                    spareMap = previous;
                }
            } else { dressedMap = null; spareMap = null; }

            var revision = (changed ? checked((m_revision + 1L)) : m_revision);
            // Construct the output before committing either journal or input cursor. The serial captured in the
            // frame never follows this mutable producer when a later frame is dressed or a consumer skips frames.
            var serial = checked((m_drawChanges.Serial + 1L));
            var dressed = (frame with {
                MeshDraws = m_draws,
                MeshDrawsRevision = revision,
                PickMap = dressedMap,
                MeshDrawChanges = m_drawChanges.Reader,
                MeshDrawChangesSerial = serial,
                MeshDrawChangesCause = (topologyChanged
                    ? (retry ? SdfMeshStagingCause.Retry : SdfMeshStagingCause.Rebuild)
                    : (portalPath ? SdfMeshStagingCause.Portal : null)),
            });

            m_drawChanges.Begin(everything: (topologyChanged || portalPath), rows: m_draws.Count);
            for (var run = 0; (run < m_changedDraws.Count); run++) {
                m_drawChanges.Owe(start: m_changedDraws.Start(index: run), count: m_changedDraws.Length(index: run));
            }
            m_dressedMap = dressedMap;
            m_spareMap = spareMap;
            m_dressed = dressed;
            m_dressedSource = frame;
            m_drawnMeshes = finished;
            m_revision = revision;
            m_copyLayoutRevision = access.CopyLayoutRevision;
            CommitInputs(frame: frame);
            CountWork(matrices: matrices, owned: owned, updated: updated, visited: visited);
            m_publicationPending = false;
            access.Acknowledge();
            return dressed;
        }
    }

    private void CountWork(int visited, int matrices, int updated, int owned) {
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.RowsOwnedChanged, amount: owned);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressPoseCandidates, amount: m_drawBindings.Count);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressBindingsVisited, amount: visited);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressMatricesComputed, amount: matrices);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressedRecordsUpdated, amount: updated);
    }
    private void CommitInputs(SdfFrame frame) {
        if (m_drawnCopies.Length != frame.MeshInstances.Count) { m_drawnCopies = new SdfMeshInstance[frame.MeshInstances.Count]; m_drawnCopyFrames = new int[frame.MeshInstances.Count]; }
        for (var index = 0; (index < m_drawnCopies.Length); index++) { m_drawnCopies[index] = frame.MeshInstances[index]; m_drawnCopyFrames[index] = frame.MeshInstances[index].FrameSlot; }
        m_hostCursor.Commit();
        m_slotCursor.Commit();
        m_bindingProgram = frame.Program;
        m_hadClips = (frame.SlotClips.Count > 0);
    }
    private void CollectSlotBindings(SdfFrame frame) {
        for (var run = 0; (run < m_changedSlots.Count); run++) {
            var end = (m_changedSlots.Start(index: run) + m_changedSlots.Length(index: run));

            for (var slot = m_changedSlots.Start(index: run); (slot < end); slot++) { AddBindings(key: (false, slot)); }
        }
    }
    private void AddBindings((bool Copy, int Index) key) {
        for (var index = m_bindingsByOwner.First(key: key); (index >= 0); index = m_bindingsByOwner.Next(row: index)) {
            m_bindingKeys.Add(item: index);
        }
    }
    // Hands the collected binding indices to the candidate runs in ascending order. Slots and copies name their bindings
    // out of binding order, and a run set takes an out-of-order index by shifting its runs.
    private void AddBindingKeys() {
        var keys = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_bindingKeys);

        keys.Sort();
        foreach (var key in keys) {
            m_bindingCandidates.Add(length: 1, start: key);
        }
        m_bindingKeys.Clear();
    }
    private void IndexBindings(SdfFrame frame) {
        m_indexedSlots = frame.DynamicTransforms.Count;
        m_bindingsByOwner.Clear();
        // A binding belongs to exactly one copy or one dynamic slot. Prepending in reverse draw order keeps each
        // adjacency chain ascending without allocating a list/object per slot or duplicating its membership.
        for (var index = (m_drawBindings.Count - 1); (index >= 0); index--) {
            var binding = m_drawBindings[index];

            if (((uint)binding.CopyIndex) < ((uint)frame.MeshInstances.Count)) {
                m_bindingsByOwner.Set(index, (true, binding.CopyIndex));
            } else if ((binding.CopyIndex < 0) && (binding.Slot is { } slot) && (((uint)slot) < ((uint)m_indexedSlots))) {
                m_bindingsByOwner.Set(key: (false, slot), row: index);
            }
        }
    }
    // Replaces, in their runs, the bindings of each group a frame replaced with its piece and copy counts standing
    // (m_replaced), as a topology rebuild would list them: every other binding, draw and row stands, the journal
    // names the rows whose staged content moved, and each such draw's claim moves with it.
    private void Replace(SdfFrame frame, SdfMeshPathBake.FrameAccess access, ref int visited, ref int matrices, ref int updated, ref int owned) {
        var reindex = false;

        foreach (var (at, group, mesh) in m_replaced) {
            var dressed = m_dressedGroups[at];

            owned += dressed.Count;
            var copies = access.CopiesOf(group: group);
            var pieces = mesh.PieceCount;
            var chunked = ((copies is null) && (mesh.Chunks.Count > 0));

            if ((dressed.Copies < 0) && (dressed.Mesh.Chunks.Count > 0)) {
                foreach (var chunk in dressed.Mesh.Chunks) { _ = m_chunkOwners.Remove(key: chunk); }
            }
            for (var offset = 0; (offset < dressed.Count); offset++) {
                var index = (dressed.Start + offset);
                var part = mesh.Piece(index: (offset % pieces));
                var copyIndex = ((copies is null) ? -1 : copies[(offset / pieces)]);
                var binding = new DrawBinding(chunked: chunked, copyIndex: copyIndex, group: group, mesh: part);
                var previous = m_drawBindings[index];
                var destination = (m_hostDrawCount + index);
                var drawn = m_draws[destination];

                reindex |= ((previous.CopyIndex != copyIndex) || (previous.Slot != binding.Slot));
                m_drawBindings[index] = binding;
                if (chunked) { m_chunkOwners[part] = group; }
                visited++;

                var draw = binding.Pose(frame: frame, matrices: ref matrices);

                if (!ReferenceEquals(objA: drawn.Mesh, objB: draw.Mesh)) { (m_drawClaims ??= new SdfMeshClaims()).Move(from: drawn.Mesh, to: draw.Mesh); }
                m_draws[destination] = draw;
                if (drawn.SameStagedContent(other: in draw)) { continue; }
                m_changedDraws.Add(length: 1, start: destination);
                updated++;
            }
            m_dressedGroups[at] = (group, mesh, dressed.Start, dressed.Count, dressed.Copies);
        }
        if (reindex) { IndexBindings(frame: frame); }
    }
    private void BuildDrawTopology(SdfFrame frame, SdfMeshPathBake.FrameAccess access, SdfMeshTopologyReason reason, ref int visited, ref int matrices, ref int owned) {
        SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.DressTopologyBuilds);
        SdfMeshHistory.ProcessWork.Count(kind: SdfMeshHistory.TopologyBuildsOf(reason: reason));
        // An owner keeps its rows when it draws what it drew: the host's list at its count, each host row its mesh, and a
        // group or an instance's own mesh every binding it held, at the run length it had.
        var host = frame.MeshDraws;

        if ((host.Count != m_hostDrawCount) || (m_draws.Count < host.Count)) {
            owned += host.Count;
        } else {
            for (var index = 0; (index < host.Count); index++) {
                if (!ReferenceEquals(objA: m_draws[index].Mesh, objB: host[index].Mesh)) { owned++; }
            }
        }
        m_previousRuns.Clear();
        foreach (var dressed in m_dressedGroups) { m_previousRuns[dressed.Group] = dressed.Count; }
        m_draws.Clear();
        m_chunkOwners.Clear();
        foreach (var binding in m_drawBindings) { m_reusableBindings.TryAdd(key: binding.Identity, value: binding); }
        m_drawBindings.Clear();
        m_dressedGroups.Clear();
        m_hostDrawCount = host.Count;
        m_draws.AddRange(collection: host);
        foreach (var group in access.Groups) {
            if (access.MeshOf(group: group) is not { } mesh) { continue; }

            var start = m_drawBindings.Count;
            var kept = true;

            if (access.CopiesOf(group: group) is { } copies) {
                foreach (var copy in copies) {
                    for (var piece = 0; (piece < mesh.PieceCount); piece++) {
                        kept &= Bind(mesh.Piece(index: piece), group, copy, chunked: false);
                    }
                }
                m_dressedGroups.Add(item: (group, mesh, start, (m_drawBindings.Count - start), copies.Count));
                owned += OwnedChange(group: group, kept: kept, start: start);
                continue;
            }
            for (var piece = 0; (piece < mesh.PieceCount); piece++) {
                var part = mesh.Piece(index: piece);

                kept &= Bind(part, group, -1, chunked: (mesh.Chunks.Count > 0));
                if (mesh.Chunks.Count > 0) { m_chunkOwners[part] = group; }
            }
            m_dressedGroups.Add(item: (group, mesh, start, (m_drawBindings.Count - start), -1));
            owned += OwnedChange(group: group, kept: kept, start: start);
        }
        for (var index = 0; (index < frame.MeshInstances.Count); index++) {
            if ((frame.MeshInstances[index].Mesh is { } mesh) && !Bind(mesh, null, index, chunked: false)) { owned++; }
        }
        IndexBindings(frame: frame);
        for (var index = 0; (index < m_drawBindings.Count); index++) {
            visited++;
            m_draws.Add(item: m_drawBindings[index].Pose(frame: frame, matrices: ref matrices));
        }
        // A binding the previous topology held for the same draw and no binding of this one took is not reused again.
        m_reusableBindings.Clear();
    }
    // Lists the binding of one draw: the previous topology's for it, or a new one. Returns whether it was the previous
    // topology's.
    private bool Bind(SdfMesh mesh, SdfMeshBakeGroup? group, int copyIndex, bool chunked) {
        var reused = m_reusableBindings.Remove(key: new DrawBinding.Key(Chunked: chunked, CopyIndex: copyIndex, Group: group, Mesh: mesh), value: out var kept);

        m_drawBindings.Add(item: (reused ? kept! : new DrawBinding(chunked: chunked, copyIndex: copyIndex, group: group, mesh: mesh)));
        return reused;
    }
    // The rows a group's new run changed the owner of: none when every binding was the previous topology's and the run
    // kept its length, the whole run otherwise.
    private int OwnedChange(SdfMeshBakeGroup group, int start, bool kept) {
        var count = (m_drawBindings.Count - start);

        return ((kept && m_previousRuns.TryGetValue(key: group, value: out var previous) && (previous == count)) ? 0 : count);
    }

    private sealed class DrawBinding(SdfMesh mesh, SdfMeshBakeGroup? group, int copyIndex, bool chunked = false) {
        // What a binding draws: its mesh or chunk and group by reference, its copy and whether its group is chunked.
        public readonly record struct Key(SdfMesh Mesh, SdfMeshBakeGroup? Group, int CopyIndex, bool Chunked);
        public sealed class KeyComparer : IEqualityComparer<Key> {
            public static KeyComparer Instance { get; } = new();

            public bool Equals(Key x, Key y) => (ReferenceEquals(objA: x.Mesh, objB: y.Mesh) && ReferenceEquals(objA: x.Group, objB: y.Group) && (x.CopyIndex == y.CopyIndex) && (x.Chunked == y.Chunked));
            public int GetHashCode(Key key) => HashCode.Combine(value1: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: key.Mesh), value2: ((key.Group is null) ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: key.Group)), value3: key.CopyIndex, value4: key.Chunked);
        }

        public Key Identity => new(Chunked: chunked, CopyIndex: copyIndex, Group: group, Mesh: mesh);

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

        public int CopyIndex => copyIndex;
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
        m_bindingsByOwner.Clear();
        m_dressedGroups.Clear();
        m_chunkOwners.Clear();
        m_drawnCopies = [];
        m_drawnCopyFrames = [];
        m_bindingProgram = null;
        m_dressed = null;
        m_dressedSource = null;
        m_dressedMap = null;
        m_spareMap = null;
    }
}
