using System.Diagnostics.CodeAnalysis;
using System.Numerics;

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
    private readonly Dictionary<SdfMeshBakeGroup, SdfMesh> m_dressedGroups = [];
    private int m_hostDrawCount;

    /// <summary>Returns a frame that draws the bake's meshes beside its own mesh draws, restarting the bake when the frame
    /// carries a new program. A dynamic group draws at its slot's transform in the frame.</summary>
    /// <param name="frame">The captured frame.</param>
    /// <returns>The frame with the baked draws appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public SdfFrame Dress(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        ObjectDisposedException.ThrowIf(m_disposed, this);
        using (var access = m_bake.PrepareFrame(frame)) {
            var finished = 0;
            var copiesChanged = (m_drawnCopies.Length != frame.MeshInstances.Count);
            var topologyChanged = ((m_hostDrawCount != frame.MeshDraws.Count) || (m_copyLayoutRevision != access.CopyLayoutRevision) || (m_productRevision != access.Revision));

            if (!copiesChanged) {
                for (var index = 0; index < m_drawnCopies.Length; index++) {
                    if ((m_drawnCopies[index] != frame.MeshInstances[index]) || (m_drawnCopyFrames[index] != frame.MeshInstances[index].FrameSlot)) { copiesChanged = true; break; }
                }
            }
            var moved = (copiesChanged || !ReferenceEquals(frame.MeshDraws, m_frameDraws) ||
                (frame.MeshDrawsRevision != m_frameDrawsRevision) || !ReferenceEquals(frame.SlotClips, m_slotClips));

            foreach (var group in access.Groups) {
                if (access.MeshOf(group) is not { } mesh) { continue; }
                finished++;
                topologyChanged |= (!m_dressedGroups.TryGetValue(group, out var heldMesh) || !ReferenceEquals(mesh, heldMesh));
            }
            topologyChanged |= (finished != m_drawnMeshes);
            // Any known movement or topology change already poses the retained bindings below. Otherwise the first
            // changed binding is enough to require that pass; HasMoved only reads poses and has no state to advance.
            var poseChecks = 0;
            if (!moved && !topologyChanged) {
                foreach (var binding in m_drawBindings) {
                    poseChecks++;
                    if (binding.HasMoved(frame)) { moved = true; break; }
                }
            }
            SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressPoseCandidates, amount: m_drawBindings.Count);
            SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.DressPoseChecks, amount: poseChecks);

            if (!moved && !topologyChanged && ReferenceEquals(frame, m_dressedSource) && (m_dressed is { } dressed)) {
                access.Acknowledge();
                return dressed;
            }
            if (moved || topologyChanged) {
                if (topologyChanged) { BuildDrawTopology(frame, access); }
                // Portal copies are derived from the same retained base records. Remove last frame's copies before
                // posing and clipping the base again, without rebuilding its geometry/copy membership.
                var baseCount = (m_hostDrawCount + m_drawBindings.Count);
                if (m_draws.Count > baseCount) { m_draws.RemoveRange(baseCount, m_draws.Count - baseCount); }
                var meshesChanged = topologyChanged;
                for (var index = 0; index < m_hostDrawCount; index++) {
                    var draw = frame.MeshDraws[index];
                    meshesChanged |= !ReferenceEquals(m_draws[index].Mesh, draw.Mesh);
                    m_draws[index] = draw;
                }
                for (var index = 0; index < m_drawBindings.Count; index++) {
                    var destination = (m_hostDrawCount + index);
                    var draw = m_drawBindings[index].Pose(frame);
                    meshesChanged |= !ReferenceEquals(m_draws[destination].Mesh, draw.Mesh);
                    if (m_draws[destination] != draw) {
                        m_draws[destination] = draw;
                        SdfMeshHistory.ProcessWork.Count(SdfMeshHistory.DressedRecordsUpdated);
                    }
                }
                SdfSlotClip.Apply(clips: frame.SlotClips, draws: m_draws);
                m_slotClips = frame.SlotClips;
                if (meshesChanged) { (m_drawClaims ??= new SdfMeshClaims()).Update(draws: m_draws); }
                if (m_drawnCopies.Length != frame.MeshInstances.Count) { m_drawnCopies = new SdfMeshInstance[frame.MeshInstances.Count]; m_drawnCopyFrames = new int[frame.MeshInstances.Count]; }
                for (var index = 0; index < m_drawnCopies.Length; index++) { m_drawnCopies[index] = frame.MeshInstances[index]; m_drawnCopyFrames[index] = frame.MeshInstances[index].FrameSlot; }
                m_frameDraws = frame.MeshDraws;
                m_frameDrawsRevision = frame.MeshDrawsRevision;
                m_drawnMeshes = finished;
                m_revision++;
            }

            // The host's map indexes the host's draws alone; a pick names the dressed list.
            if (frame.PickMap is { } hostMap) {
                if (!Dresses(map: m_dressedMap, hostMap: hostMap, hostDraws: frame.MeshDraws.Count)) {
                    // A body straddling a door adds and drops its carried copies tick by tick, so the map before this one
                    // is kept and taken back when the list returns to it.
                    var previous = m_dressedMap;

                    m_dressedMap = (Dresses(map: m_spareMap, hostMap: hostMap, hostDraws: frame.MeshDraws.Count)
                        ? m_spareMap
                        : new SdfDressedPickMap(chunkOwners: m_chunkOwners, draws: m_draws, hostDraws: frame.MeshDraws.Count, inner: hostMap));
                    m_spareMap = previous;
                }
            } else {
                m_dressedMap = null;
                m_spareMap = null;
            }
            m_dressed = (frame with { MeshDraws = m_draws, MeshDrawsRevision = m_revision, PickMap = m_dressedMap });

            m_dressedSource = frame;
            m_productRevision = access.Revision;
            m_copyLayoutRevision = access.CopyLayoutRevision;
            access.Acknowledge();
            return m_dressed;
        }
    }

    private void BuildDrawTopology(SdfFrame frame, SdfMeshPathBake.FrameAccess access) {
        SdfMeshHistory.ProcessWork.Count(SdfMeshHistory.DressTopologyBuilds);
        m_draws.Clear();
        m_chunkOwners.Clear();
        m_drawBindings.Clear();
        m_dressedGroups.Clear();
        m_hostDrawCount = frame.MeshDraws.Count;
        m_draws.AddRange(frame.MeshDraws);
        foreach (var group in access.Groups) {
            if (access.MeshOf(group) is not { } mesh) { continue; }
            m_dressedGroups.Add(group, mesh);
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
        void Bind(DrawBinding binding) {
            m_drawBindings.Add(binding);
            m_draws.Add(binding.Pose(frame));
        }
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
        public bool HasMoved(SdfFrame frame) {
            if ((copyIndex >= 0) || (group is not { Dynamic: true })) { return false; }
            var (position, orientation, scale) = PoseOf(frame);
            return !m_posed || (position != m_position) || (orientation != m_orientation) || (scale != m_scale);
        }

        public SdfMeshDraw Pose(SdfFrame frame) {
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
                return m_draw = new SdfMeshDraw(Identity: m_pickIdentity, Material: ((group is null) ? copy.Material : 0),
                    Mesh: ((group is null) ? copy.Mesh! : mesh), ObjectToWorld: copy.Matrix) {
                    FrameSlot = copy.FrameSlot, UsesRootMaterialFrame = copy.UsesRootMaterialFrame, Glass = (group?.Glass ?? false), IsDynamic = copy.IsDynamic,
                    MotionIdentity = m_motionIdentity,
                };
            }
            var (position, orientation, scale) = PoseOf(frame);
            if (m_posed && (m_position == position) && (m_orientation == orientation) && (m_scale == scale) && (m_draw.FrameSlot == (group!.Dynamic ? group.Slot : null)) && (m_draw.Glass == group.Glass)) { return m_draw; }
            m_position = position;
            m_orientation = orientation;
            m_scale = scale;
            m_posed = true;
            var matrix = (group!.Dynamic
                ? ((Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(orientation)) * Matrix4x4.CreateTranslation(position))
                : Matrix4x4.Identity);
            return m_draw = new SdfMeshDraw(Identity: (chunked ? mesh : group), Material: 0, Mesh: mesh,
                ObjectToWorld: matrix) { FrameSlot = (group.Dynamic ? group.Slot : null), Glass = group.Glass, IsDynamic = group.Dynamic };
        }
    }

    // The straddling slot runs the dressed draws were last clipped by.
    private IReadOnlyList<SdfSlotClip> m_slotClips = [];

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
        m_dressedGroups.Clear();
        m_chunkOwners.Clear();
        m_drawnCopies = [];
        m_dressed = null;
        m_dressedSource = null;
        m_dressedMap = null;
        m_spareMap = null;
    }
}
