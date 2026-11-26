using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

// The residency's mesh draws: the frame's draw records (SdfFrame.MeshDraws) in one GpuRegion of the tables' own, created
// like every other table (SdfWorldTables.Regions.cs) and copied with them in the upload, and the names those records
// hold in the presentation's one mesh region (SdfMeshRegionStore), where every mesh lies once however many residencies
// draw it. A frame whose draw list is the one last packed, at the revision and program composability last packed, packs
// nothing, and any other packs only the records that changed: a body that moves rewrites its record's matrix, and a mesh
// that lands in the store has the rows naming it staged again, in this residency and every other that names it
// (NoteStoreChange). A draw whose mesh does not stand in the store keeps its producer row as an absent record until it
// lands, so a boot or a body crossing into a world with all its meshes at once fills in over several frames from the
// cameras outward rather than staging every word in one upload. A mesh that replaces one a draw reads is named ahead of
// the draws (NameMeshesAhead) and lands beside the one it replaces, which the draws read until the new mesh stands
// (MeshStands), so the swap holds no draw out of a frame. A view's mesh pass reads a draw's record from the draw region
// and its triangles from the store's.
//
// Invariant: a row's record names a mesh's words only while the store answers that the mesh stands (MeshStands), read
// under the store's gate in the same staging that packs the record. StageMeshDraw is the one place a row is staged live
// or absent, and StageMeshRegion and StageLandedMeshes are the only doors to it; both hold the store's gate from the
// row's names to its records, so another residency's plan never moves a mesh between the two.
public sealed partial class SdfWorldTables {
    private readonly SdfMeshRegionStore m_meshStore;
    // The names this residency holds ahead of its draws in the store, as it last named them (NameMeshesAhead).
    private readonly List<(SdfMesh Mesh, bool Product)> m_meshesAhead = [];

    /// <summary>Gets the presentation's mesh region the residency's draws name their meshes in.</summary>
    public SdfMeshRegionStore MeshStore => m_meshStore;

    // Called only during a background admission on the frame thread. An owed geometry is never bound.
    internal bool TryProbeScene(SdfMeshIrradianceScene scene, out IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, out IGpuBuffer meshes, out long version) {
        var bound = m_meshStore.TryProbeScene(scene: scene, placements: out placements, meshes: out meshes, version: out version);

        return (BindsBackgroundWork && bound);
    }
    // A staged region of the probe service's (the trace scene, the feedback volume), named by its part so the memory reports
    // attribute each apart. A mapped region (GpuRegion.Mapped) grows in place; only one no kernel sizes by its buffer's
    // length may be one, since a mapped buffer's length is the range it reserves. A region whose words come from a source
    // states the host copy it keeps (hostCopyBytes), so it never allocates a whole one to give up.
    internal GpuRegion CreateProbeRegion(string part, int bytes, int slots = 1, bool mapped = false, long? hostCopyBytes = null) => new(
        policy: GpuResidencyPolicy.Staged, memory: GpuResidency.RingMemory(profile: m_deviceContext.MemoryProfile),
        byteCount: bytes, slotCount: slots, buffers: m_gpu.BufferFactory, bindings: m_gpu.Bindings,
        recorder: m_gpu.Recorder, copyPipeline: m_regionCopyPipeline, name: NameOf(part), mapped: mapped, hostCopies: m_regionHostCopies,
        hostCopyBytes: hostCopyBytes);

    private SdfMeshClaims? m_meshClaims;

    /// <summary>Polls retired device objects on the frame thread, including for hidden residencies, and serves what the
    /// presentation's mesh region owes outside a plan (<see cref="SdfMeshRegionStore.Service"/>), then brings the sets to
    /// a region buffer another residency replaced, so a residency nothing prepares lets the old one go.</summary>
    internal void ServiceMemoryTrim() {
        ServiceRetirements();
        m_shadows?.Upkeep();
        m_meshStore.Service();
        m_meshStore.ProbeScenesIfCreated?.Service();
        BindMeshStore();
    }

    /// <summary>Gets whether the latest staged draws name a mesh whose words the store still owes: the mesh lands through
    /// the background GPU work, and its draws stay absent until it does. It turns false as the last such mesh lands,
    /// before a staging names its draws (<see cref="DrawsAwaitMeshes"/>). Safe to read from any thread.</summary>
    public bool MeshesLanding => (Volatile.Read(location: ref m_owedWords) != 0);
    /// <summary>Gets whether a producer row is staged absent because its mesh is owed or refused. Read on the frame thread.</summary>
    public bool DrawsAwaitMeshes => (m_absentRows.Count > 0);
    /// <summary>Gets whether the staged draws' meshes all stand: the store owes none of their words, refuses none of
    /// them, and every copy it owed has completed. Pose, draw-record and irradiance uploads do not delay it. Read on the
    /// frame thread; never waits.</summary>
    public bool MeshesResident => (MeshResidencyPending() is null);
    /// <summary>Gets whether the staged draws' meshes are still on their way to the store: words it owes, which land
    /// through background GPU work, or a copy not yet submitted or complete. Unlike <see cref="MeshesResident"/> it
    /// ignores the meshes the store refuses under its limit, which no frame brings. Read on the frame thread; never
    /// waits.</summary>
    public bool MeshesArriving => (!m_disposed && ((Volatile.Read(location: ref m_owedWords) != 0) || !m_meshStore.CopiesComplete));

    /// <summary>Names what keeps <see cref="MeshesResident"/> false: the owed words of the staged draws' meshes and the
    /// store's landing, the meshes the store refuses under its limit, or a copy the store still owes;
    /// <see langword="null"/> once resident. Read on the frame thread; never waits.</summary>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public string? MeshResidencyPending() {
        if (m_disposed) { return "the tables are disposed"; }
        var owed = Volatile.Read(location: ref m_owedWords);

        if (owed != 0) {
            return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler:
                $"owes {(owed * sizeof(uint))}B of its draws' meshes; the store holds {m_meshStore.DescribePending()} binds-background={BindsBackgroundWork} uploads={m_uploads} binding-revision={m_bindingRevision}/{m_uploadedBindingRevision}");
        }
        if (MeshRegionRefused > 0) {
            return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler:
                $"the mesh region refuses {MeshRegionRefused} of the draws' meshes: the staged list names {MeshRegionNamedBytes}B, and the region is limited to {MeshRegionLimitBytes}B");
        }
        return (m_meshStore.CopiesComplete ? null : "the mesh region owes a copy no upload has completed");
    }

    // The draw list last planned and its revision, and whether its draws are all staged: a list whose meshes' words are
    // still owed stages again each frame as they land.
    private object? m_meshSource;
    private long m_meshSourceRevision;
    private long m_meshSourceSerial;
    private object? m_meshCauseSource;
    private bool m_meshStaged;

    // Consumer identity, captured endpoint and bounds are owned by the cursor; landing never changes row indices.
    private readonly GpuRowCursor m_drawCursor = new();
    private readonly GpuRowIndex<SdfMesh> m_rowsByMesh = new(comparer: ReferenceEqualityComparer.Instance);
    // The rows whose mesh's standing or address changed in the store since they were last staged (NoteStoreChange),
    // written and read under the store's gate.
    private readonly GpuUploadRuns m_landedRows = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly HashSet<int> m_absentRows = [];
    private readonly GpuUploadRuns m_owedDraws = new(capacity: GpuRegion.MaxCopyRuns);
    // Whether the store changed a mesh a row names since the draws were last staged: a frame that stands is staged again
    // then (StageLandedMeshes).
    private volatile bool m_meshLandedSinceStaging;
    // The rows that name each refused mesh are few; the count of this residency's named meshes the store refuses is
    // read from the store when asked.
    private int m_meshDrawCapacity;

    /// <summary>Gets the logical array payload retained by this consumer's changed-draw and transform-cut runs.</summary>
    public long DrawChangeScratchBytes => (((((m_owedDraws.StorageBytes + m_landedRows.StorageBytes) + m_rowsByMesh.StorageBytes) +
        m_meshTransformChanges.StorageBytes) + m_meshTransformReseats.StorageBytes) + m_reseatedDraws.StorageBytes);

    /// <summary>Tells the tables that the store changed the standing or the address of a mesh their rows name (it
    /// landed, moved, or lost its place): the rows naming it are staged again before the residency next draws. Called
    /// by the store under its gate, on whichever thread changed it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="moved">Whether the mesh's words moved: its rows are packed again though it stood before and
    /// stands now, since their records hold the words it left.</param>
    internal void NoteStoreChange(SdfMesh mesh, bool moved) {
        for (var row = m_rowsByMesh.First(key: mesh); (row >= 0); row = m_rowsByMesh.Next(row: row)) { m_landedRows.Add(length: 1, start: row); }
        if (moved) { _ = m_meshesMoved.Add(item: mesh); }
        m_meshLandedSinceStaging = true;
    }
    // The meshes another residency's plan moved since this one last staged, under the store's gate: a staging packs
    // their rows again and then forgets them (FinishMeshStaging).
    private readonly HashSet<SdfMesh> m_meshesMoved = new(comparer: ReferenceEqualityComparer.Instance);

    // Whether a draw record naming a mesh holds words the mesh has left: the plan this staging made moved it, or
    // another residency's plan did since this one last staged.
    private bool MeshMoved(SdfMesh mesh) => m_meshesMoved.Contains(item: mesh);
    /// <summary>Tells the tables that the store's region answers another buffer, so every set that binds it is written
    /// again. Called by the store under its gate.</summary>
    internal void NoteStoreBuffer() => Interlocked.Increment(location: ref m_meshStoreBuffers);
    private long m_meshStoreBuffers;
    private long m_meshStoreBuffersBound;
    /// <summary>Gets whether a set of these tables may still name a region buffer the store replaced: the store has
    /// replaced it since the tables last moved their binding revision for it. The store holds a replaced buffer while any
    /// residency answers true (<see cref="SdfMeshRegionStore"/>'s retirement), since a view recorded with such a set reads
    /// the old buffer. Read by the store under its gate.</summary>
    internal bool NamesReplacedStoreBuffer => (Interlocked.Read(location: ref m_meshStoreBuffers) != Volatile.Read(location: ref m_meshStoreBuffersBound));

    // Brings the sets to the store's buffer where it was replaced since they were last written. The revision moves before
    // the bound count, so the store never finds these tables bound to the new buffer while their sets still stand.
    private void BindMeshStore() {
        var buffers = Interlocked.Read(location: ref m_meshStoreBuffers);

        if (buffers == m_meshStoreBuffersBound) { return; }
        m_bindingRevision++;
        Volatile.Write(location: ref m_meshStoreBuffersBound, value: buffers);
    }
    /// <summary>Adds to the words of the staged draws' meshes the store owes. Called by the store under its gate.</summary>
    /// <param name="delta">The words to add; negative as a mesh lands or a row stops naming it.</param>
    internal void AddOwedWords(long delta) {
        _ = Interlocked.Add(location1: ref m_owedWords, value: delta);
        AccountMeshOwed();
    }
    /// <summary>Adds to this residency's part of the words its draws name in the store, a mesh several residencies name
    /// counted in equal parts. Called by the store under its gate.</summary>
    /// <param name="delta">The words to add.</param>
    internal void AttributeNamedWords(double delta) {
        m_meshNamedWords += delta;
        Volatile.Write(location: ref m_meshRegionNamedBytes, value: ((ulong)Math.Max(val1: 0d, val2: (m_meshNamedWords * sizeof(uint)))));
    }
    private double m_meshNamedWords;
    /// <summary>Measures a mesh's landing priority from this residency's rows naming it: the highest priority among
    /// them at its demand points, and the first row at it. Called by the store under its gate, from this residency's
    /// own staging alone, since it reads the residency's draws and transforms.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="best">The highest priority so far, raised by a row that passes it.</param>
    /// <param name="first">The first row at the highest priority so far.</param>
    /// <returns>The rows measured.</returns>
    internal int MeasureLanding(SdfMesh mesh, ref float best, ref int first) {
        var demand = ((MeshDemand is { Count: > 0 } points) ? points : null);
        var draws = m_currentDrawRows;
        var rows = 0;

        for (var row = m_rowsByMesh.First(key: mesh); (row >= 0); row = m_rowsByMesh.Next(row: row)) {
            if (row >= draws.Count) { continue; }
            var draw = draws[row];
            var priority = ((demand is null) ? 0f : (SdfMeshDemand.BoxOf(center: out var center, draw: in draw, half: out var half, transforms: m_motionTransforms) ? demand.Priority(center: center, half: half) : float.PositiveInfinity));

            if ((first < 0) || (priority > best) || ((priority == best) && (row < first))) {
                best = priority;
                first = row;
            }
            rows++;
        }
        return rows;
    }

    // A copy of each producer draw last staged, including rows staged absent: the comparison for changed runs, and the
    // name each row holds in the store.
    private SdfMeshDraw[] m_stagedDraws = [];

    private int m_stagedCount;

    private ulong[] m_stagedOccupancies = [];
    // Per staged row, its static caster's (SdfMeshDraw.SameCaster) state since the caster last changed: CasterNeverStood,
    // CasterStands, or the value of m_castersRead when its mesh went absent. A landing moves the static revision when the
    // caster never stood, or when a pass read the casters (CastersRead) while it was absent; a tier swap or an eviction no
    // pass saw leaves the held map's shadow standing for the same caster.
    private long[] m_stagedCasterState = [];
    private const long CasterNeverStood = long.MinValue;
    private const long CasterStands = -1;
    // One more for every recording of a pass that draws the static casters alone (the distant shadow map, the far
    // cascade's static layer).
    private long m_castersRead;

    /// <summary>Records that a pass drawing the static casters alone and holding them (the distant shadow map, the far
    /// cascade's static layer) recorded
    /// over the staged records as they stand now: a caster absent now and landing later moves
    /// <see cref="StaticDrawRevision"/>, since the map may have been drawn without it. Called on the frame thread by the
    /// recording.</summary>
    internal void CastersRead() => m_castersRead++;

    private SdfMeshDraws m_currentDrawRows;

    // The draw records' region.
    private readonly GpuRegion m_meshRegion;

    // One more for every draw record a staging writes. The whole view's signature folds it; a pass signs with its motion
    // scope's revision instead (SdfMotionScope), which moves only for the records the pass can show.
    private long m_meshRevision;

    /// <summary>Gets the revision of the staged draw records: one more for every record a staging wrote. Read on the
    /// frame thread.</summary>
    public long MeshRevision => m_meshRevision;

    // Every table's static revisions come from one sequence, so tables replaced after a device loss never repeat one an
    // earlier table published and a held distant map drawn from the old records never stands for the new ones.
    private static long StaticDrawRevisions;
    private long m_staticDrawRevision = Interlocked.Increment(location: ref StaticDrawRevisions);

    /// <summary>Gets the revision of the static scene's casters (<see cref="SdfMeshDraw.IsStatic"/>): a new value whenever
    /// a staging adds or removes such a draw, changes its caster (<see cref="SdfMeshDraw.SameCaster"/>: its placement,
    /// material or clip, or its mesh other than by the tier of one bake group), or lands a caster that has not stood since
    /// it changed, and never otherwise, so a moving body, a pose, a mesh the store only moved, a tier swap the view or
    /// the memory budget makes and an eviction leave it as it was. Unique across every table of the process. A pass that
    /// draws static casters alone at a texel coarser than a tier's step and holds them (the distant shadow map) draws again
    /// when it changes. Read on the frame thread.</summary>
    public long StaticDrawRevision => m_staticDrawRevision;

    private void StaticDrawsChanged() => m_staticDrawRevision = Interlocked.Increment(location: ref StaticDrawRevisions);

    internal SdfMeshRowStamp RowStamp => new(m_currentDrawRows.Rows, m_meshSourceRevision);

    // An explicit publication cause, or null to classify each staged row as a hand-over, landing or pose (StagingTally).
    private SdfMeshStagingCause? m_stagingCause;
    // The draws the frame being rendered rasterizes, from its staged draw list, and how many of them are glass.
    private uint m_meshDrawCount;
    private uint m_glassDrawCount;

    // The draw records as the next write sends them.
    private uint[] m_meshDrawWords = [];
    private readonly GpuUploadRuns m_meshDrawDifferences = new(capacity: GpuRegion.MaxCopyRuns, mergeGap: GpuRegion.StagedMergeGapWords);

    /// <summary>The fewest draw records the draw region holds.</summary>
    public const int MinimumDrawCapacity = 256;

    /// <summary>Gets the bytes the presentation's mesh region holds, every residency's meshes together. Safe to read from
    /// any thread.</summary>
    public ulong MeshRegionBytes => ((ulong)m_meshStore.Bytes);
    /// <summary>Gets the bytes of the presentation's mesh region its placed meshes use, every residency's together.
    /// Safe to read from any thread.</summary>
    public ulong MeshRegionUsedBytes => ((ulong)m_meshStore.UsedBytes);
    /// <summary>Gets the residency's grant from its presentation's selector, which its bake reads its budget and bias
    /// from.</summary>
    public SdfResidencyGrant Grant => m_meshGrant;
    /// <summary>Gets the most bytes the presentation's mesh region places, as its grant last reserved them before a plan
    /// (<see cref="SdfResidencyGrant.ReserveGrowth"/>). A mesh a draw names that finds no room under it is refused, and
    /// its draws wait until a later plan fits it.</summary>
    public ulong MeshRegionLimitBytes => ((ulong)m_meshStore.LimitBytes);

    // The residency's part of its presentation's selector: the budget and bias its bake holds its detail to.
    private readonly SdfResidencyGrant m_meshGrant;

    /// <summary>Gets this residency's part of the bytes its draws name in the presentation's mesh region: every mesh they
    /// name, placed or refused, a mesh other residencies name too counted in equal parts, so the residencies' parts
    /// together are the bytes the names hold. Safe to read from any thread.</summary>
    public ulong MeshRegionNamedBytes => Volatile.Read(location: ref m_meshRegionNamedBytes);
    /// <summary>Gets how many of the meshes the staged draws name the store refuses under its limit. Read on the frame
    /// thread.</summary>
    public int MeshRegionRefused {
        get {
            if (m_meshStore.RefusedCount == 0) { return 0; }
            var refused = 0;

            lock (m_meshStore.Gate) {
                foreach (var row in m_absentRows) {
                    if ((row < m_stagedCount) && (m_stagedDraws[row].Mesh is { } mesh) && m_meshStore.Refuses(mesh: mesh)) { refused++; }
                }
            }
            return refused;
        }
    }

    private ulong m_meshRegionNamedBytes;

    /// <summary>Gets the draws the latest frame's mesh passes rasterize in each view, the glass draws among them.</summary>
    public uint MeshDrawCount => m_meshDrawCount;
    /// <summary>Gets how many of <see cref="MeshDrawCount"/> are glass (<see cref="SdfMeshDraw.Glass"/>): the glass pass
    /// draws them, and the mesh pass and the shadow cascades draw the rest.</summary>
    public uint GlassDrawCount => m_glassDrawCount;
    /// <summary>Gets the opaque draws the latest frame's mesh pass and shadow cascades rasterize.</summary>
    public uint OpaqueDrawCount => (m_meshDrawCount - m_glassDrawCount);

    // The staging door maintains exactly the live glass rows; a pose or landing needs no glass roster scan.
    private readonly HashSet<int> m_glassIndices = [];

    /// <summary>Returns whether the glass pass would draw any glass draw of the staged list under a cull: the test the
    /// pass's own selection applies to the box each draw's record holds (<see cref="SdfMeshCull.Keeps"/>), so a draw no
    /// host box bounds counts as drawn and an absent one does not.</summary>
    /// <param name="cull">The glass pass's cull.</param>
    /// <returns><see langword="false"/> when the pass keeps no glass draw, so it rasterizes nothing.</returns>
    public bool GlassMaySee(in SdfMeshCull cull) {
        if (m_glassDrawCount == 0) {
            return false;
        }
        foreach (var index in m_glassIndices) {
            if (RowSelection(row: index, box: out var box, glass: out _) && cull.Keeps(box: in box)) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the rows of a view's render grid the glass pass may write: the rows every staged glass draw's box
    /// spans in the view's projection, widened by <paramref name="margin"/> rows for the grid's jitter and the raster's
    /// coverage. No cull leaves a draw out, so the rows hold whatever the pass's own selection keeps; a box wholly behind
    /// the eye is clipped by the near plane and spans none, and a box crossing the near plane, or a draw no host box bounds,
    /// spans every row.</summary>
    /// <param name="camera">The view's camera, which the glass pass projects with.</param>
    /// <param name="height">The rows down the view's render grid.</param>
    /// <param name="margin">The rows each span is widened by on each side.</param>
    /// <returns>The first and last row, inclusive, or <see langword="null"/> when no glass draw spans a row.</returns>
    public (uint First, uint Last)? GlassRows(in CameraSnapshot camera, uint height, uint margin) {
        if ((m_glassDrawCount == 0) || (height == 0)) {
            return null;
        }

        var top = float.PositiveInfinity;
        var bottom = float.NegativeInfinity;

        foreach (var index in m_glassIndices) {
            if (!RowSelection(row: index, box: out var box, glass: out _)) {
                continue;
            }
            if (box is not { } bounded) {
                return (0u, (height - 1u));
            }

            var behind = 0;
            var spanTop = float.PositiveInfinity;
            var spanBottom = float.NegativeInfinity;

            for (var corner = 0; (corner < 8); corner++) {
                var point = (((bounded.Center
                    + (((corner & 1) != 0) ? bounded.HalfX : -bounded.HalfX))
                    + (((corner & 2) != 0) ? bounded.HalfY : -bounded.HalfY))
                    + (((corner & 4) != 0) ? bounded.HalfZ : -bounded.HalfZ));

                if (!camera.TryProject(world: point, ndc: out var ndc, depth: out var forward) || (forward < camera.Near)) {
                    behind += ((forward <= 0f) ? 1 : 9);
                    continue;
                }

                // The row a point projects to, as the kernels map a ray to a pixel: the first row's top at zero, Y down.
                var row = ((0.5f - (0.5f * ndc.Y)) * height);

                spanTop = MathF.Min(x: spanTop, y: row);
                spanBottom = MathF.Max(x: spanBottom, y: row);
            }
            if (behind == 8) {
                continue;
            }
            if (behind != 0) {
                return (0u, (height - 1u));
            }

            top = MathF.Min(x: top, y: spanTop);
            bottom = MathF.Max(x: bottom, y: spanBottom);
        }
        if (!(bottom >= top)) {
            return null;
        }

        var first = Math.Clamp(value: (MathF.Floor(x: top) - margin), min: 0f, max: (height - 1f));
        var last = Math.Clamp(value: (MathF.Ceiling(x: bottom) + margin), min: 0f, max: (height - 1f));

        return ((uint)first, (uint)last);
    }


    // Grows the draw region to hold a list's records: doubled, at least the list and MinimumDrawCapacity, at most
    // SdfMeshRegion.MaxDraws. The next upload carries the records the old buffer held on the GPU, and every set that
    // binds the region is written again. Returns the rows the region holds records for: the whole list, or, when the
    // account or the device refuses the growth, the rows the standing region fits, the first rows of the list. The rest
    // are shed for the staging (StageMeshRegion stages, names and draws none of them) and owed to the next staging,
    // which asks for the growth again, so the refusal costs the presentation the tail of its draw list for as long as it
    // stands, never the presentation. Decided before the store's transaction opens, so no name is taken for a shed row.
    private int EnsureMeshDrawCapacity(int count) {
        if (count <= m_meshDrawCapacity) { return count; }

        var capacity = ((int)GpuCapacity.Grown(ceiling: SdfMeshRegion.MaxDraws, current: m_meshDrawCapacity, divisor: 1, minimum: MinimumDrawCapacity, required: count));
        var bytes = (((long)capacity) * SdfMeshRegion.DrawBytes);

        if (bytes > m_meshRegion.ByteCount) {
            m_retired.Reserve();
            try {
                Retire(replaced: m_meshRegion.Grow(byteCount: bytes), carryingRegion: m_meshRegion);
            } catch (GpuMemoryRefusedException) {
                m_drawShedStagings++;
                m_drawsShed = (count - m_meshDrawCapacity);
                return m_meshDrawCapacity;
            }
            m_seedMeshHistory = true;
            m_bindingRevision++;
        }
        m_meshDrawCapacity = capacity;
        m_drawsShed = 0;
        m_meshGrant.Floor(bytes: bytes);
        return count;
    }

    // The rows the latest staging shed because the draw region could not grow to hold them, and the stagings that shed
    // any (EnsureMeshDrawCapacity).
    private int m_drawsShed;
    private long m_drawShedStagings;

    // Packs new draws or a new revision: names the meshes its rows draw in the store, has the store place them, and
    // packs the records that changed from the store's placements; the upload sends the slot what it owes. The store's
    // gate is held from the names to the records, so every record is packed from the plan its names made.
    private void StageMeshRegion(SdfFrame frame) {
        var draws = frame.DrawRows;

        ServiceMemoryTrim();
        SdfMeshHistory.ProcessWork.Raise(kind: SdfMeshHistory.PeakRowsExtent, value: draws.Count);
        PrepareMeshTransformCuts(frame: frame);
        // The rows the draw region holds records for: the list, or its first rows when a growth was refused.
        var count = EnsureMeshDrawCapacity(count: draws.Count);
        lock (m_meshStore.Gate) {
            m_currentDrawRows = draws;
            var whole = m_drawCursor.Collect(draws.Changes, draws.Serial, draws.Roster, draws.Count,
                draws.Revision, m_owedDraws, force: !m_meshStaged);

            if (whole) { SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.TablesWhole); }
            m_owedDraws.Append(source: m_reseatedDraws);
            if (count > m_stagedCount) { m_owedDraws.Add(m_stagedCount, (count - m_stagedCount)); }
            m_meshClaims ??= new SdfMeshClaims();
            if (m_stagedDraws.Length < count) {
                var capacity = ArrayCapacity.Grown(m_stagedDraws.Length, count);

                Array.Resize(array: ref m_stagedDraws, newSize: capacity); Array.Resize(array: ref m_stagedOccupancies, newSize: capacity);
                Array.Resize(array: ref m_stagedCasterState, newSize: capacity);
            }
            for (var row = count; (row < m_stagedCount); row++) { NoteUnnamedRow(row: row); Unname(row: row); }
            for (var run = 0; (run < m_owedDraws.Count); run++) {
                var end = Math.Min(val1: count, val2: (m_owedDraws.Start(index: run) + m_owedDraws.Length(index: run)));

                for (var row = m_owedDraws.Start(index: run); (row < end); row++) {
                    var old = m_stagedDraws[row].Mesh;
                    var mesh = draws[row].Mesh;
                    var occupantChanged = ((draws.Occupancy(row: row) != m_stagedOccupancies[row]) ||
                        ((draws.Rows is not null) && !ReferenceEquals(objA: m_meshCauseSource, objB: draws.Roster)));

                    if (ReferenceEquals(objA: old, objB: mesh) && !occupantChanged) { continue; }
                    RowChanged(row: row);
                    if (ReferenceEquals(objA: old, objB: mesh)) { m_meshStore.TouchLanding(mesh: mesh); continue; }
                    // The new name is taken before the old one goes, so a mesh two rows swap keeps its place. The row's
                    // staged draw takes the new mesh when its record is packed below, which a changed mesh always is.
                    if (mesh is not null) { m_meshStore.Name(client: this, mesh: mesh); m_meshClaims.Claim(mesh: mesh); m_rowsByMesh.Set(key: mesh, row: row); } else { m_rowsByMesh.Remove(row: row); }
                    if (old is not null) { m_meshStore.Unname(client: this, mesh: old); m_meshClaims.Release(mesh: old); }
                }
            }
            m_meshStore.Plan(planner: this);
            // The plan may replace the region's buffer, and the records below are packed from its placements: the sets
            // these records are read through bind the buffer the plan left, never the one before it.
            BindMeshStore();
            m_owedDraws.Append(source: m_landedRows);
            var words = (count * SdfMeshRecord.DrawWords);

            ArrayCapacity.EnsureLength(ref m_meshDrawWords, words);
            m_stagingCause = (((draws.Serial != m_meshSourceSerial) || !ReferenceEquals(objA: m_meshCauseSource, objB: draws.Roster))
                ? draws.Cause : null);
            _ = StageChangedDraws(candidates: m_owedDraws, count: count, draws: draws);
            if (m_meshDrawCount != ((uint)count)) { m_meshRevision++; }
            m_meshDrawCount = ((uint)count);
            m_glassDrawCount = ((uint)m_glassIndices.Count);
            m_stagedCount = count;
            m_meshHistory.RetireAfter(count: count);
            EnsurePreviousMeshCapacity(count: count);
            m_meshSource = draws.Roster; m_meshCauseSource = draws.Roster; m_meshSourceRevision = draws.Revision; m_meshSourceSerial = draws.Serial; m_meshStaged = true;
            m_meshStore.RankLanding(client: this, sweep: true);
            EndMotionStaging();
            FinishMeshStaging(frame: frame);
        }
    }
    private void Unname(int row) {
        RowChanged(row: row);
        if (m_stagedDraws[row].IsStatic) { StaticDrawsChanged(); }
        if (m_stagedDraws[row].Mesh is { } mesh) { m_meshStore.Unname(client: this, mesh: mesh); m_meshClaims!.Release(mesh: mesh); }
        m_rowsByMesh.Remove(row: row); m_absentRows.Remove(item: row); m_glassIndices.Remove(item: row);
        m_stagedDraws[row] = default; m_stagedOccupancies[row] = 0; m_stagedCasterState[row] = CasterNeverStood;
    }
    private void FinishMeshStaging(SdfFrame frame) {
        AccountMeshOwed(frame: true);
        CheckMeshRows(frame: frame);
        if ((m_skipDrawThrough > 0) && (frame.DrawRows.Serial <= m_skipDrawThrough)) {
            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Skipped);
        } else { m_drawCursor.Commit(); m_skipDrawThrough = 0; }
        m_landedRows.Clear();
        m_meshesMoved.Clear();
        m_meshLandedSinceStaging = false;
        CommitMeshTransformCuts(frame: frame);
    }

    /// <summary>Stages again the draws of the frame the tables last packed when the store changed a mesh its draws name
    /// since (one landed, moved or lost its place), so a frame that stands draws them as they stand; does nothing
    /// otherwise. Staging the same frame again consumes no transform or draw change a staging already consumed.</summary>
    /// <param name="frame">The frame the tables last packed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public void StageLandedMeshes(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        if (m_disposed || !m_meshLandedSinceStaging || !ReferenceEquals(objA: frame.DrawRows.Roster, objB: m_meshSource) || (frame.DrawRowsRevision != m_meshSourceRevision)) { return; }
        BeginMotionStaging(frame: frame);
        StageMeshRegion(frame: frame);
    }

    // Stages the records of the draws among the first count rows the candidates name (every one of them without
    // candidates); count is the rows the draw region holds records for (EnsureMeshDrawCapacity).
    private int StageChangedDraws(SdfMeshDraws draws, int count, GpuUploadRuns? candidates = null) {
        var records = m_meshDrawWords.AsSpan(length: (count * SdfMeshRecord.DrawWords), start: 0);
        m_meshDrawDifferences.Clear();
        var changedRecords = 0;
        var changedWords = 0L;
        var comparedRecords = 0;
        var tally = new StagingTally(cause: m_stagingCause);

        for (var run = 0; (run < (candidates?.Count ?? 1)); run++) {
            var start = (candidates?.Start(index: run) ?? 0);
            var end = ((candidates is null) ? count : Math.Min(val1: count, val2: checked((start + candidates.Length(index: run)))));

            for (var index = start; (index < end); index++) {
                var draw = draws[index];

                comparedRecords++;

                var sameRoster = (ReferenceEquals(objA: m_meshSource, objB: draws.Roster) && (draws.Occupancy(row: index) == m_stagedOccupancies[index]));
                var difference = draw.StagedDifference(other: in m_stagedDraws[index]);
                // A draw that moved with the slot it rides has the record it had: the kernels pose it from the slot's row,
                // and the motion scopes judge it as that slot moves (CollectMovedSlots).
                var recordStands = ((difference == SdfMeshDrawDifference.None) || (difference == SdfMeshDrawDifference.Slot));

                if (!sameRoster || !recordStands ||
                    ((draw.Mesh is not null) && (m_absentRows.Contains(item: index) == MeshStands(mesh: draw.Mesh))) || ((draw.Mesh is not null) && MeshMoved(mesh: draw.Mesh)) ||
                    (m_reseatedDraws.Contains(value: index) && MeshTransformWasReseated(draw: in draw))) {
                    var record = records.Slice(length: SdfMeshRecord.DrawWords, start: (index * SdfMeshRecord.DrawWords));
                    ref readonly var previous = ref m_stagedDraws[index];

                    // What the static scene casts changed: a static row's caster (SdfMeshDraw.SameCaster: its placement,
                    // material and clip, its mesh up to the tier of one bake group) appeared or changed, or it lands
                    // where a held map may lack it (m_stagedCasterState). A relocation, an occupancy change, and a tier
                    // swap or an eviction of a caster that stood while no pass read the casters cast the same shadow.
                    var stands = ((draw.Mesh is not null) && MeshStands(mesh: draw.Mesh));

                    // An owed mesh's draw that changed may change where its mesh lands in the order.
                    if (!stands) { m_meshStore.TouchLanding(mesh: draw.Mesh); }

                    if (draw.IsStatic || previous.IsStatic) {
                        ref var state = ref m_stagedCasterState[index];

                        if ((index >= m_stagedCount) || (draw.IsStatic != previous.IsStatic) || !draw.SameCaster(other: in previous)) {
                            StaticDrawsChanged();
                            state = ((draw.IsStatic && stands) ? CasterStands : CasterNeverStood);
                        } else if (stands) {
                            if ((state == CasterNeverStood) || ((state >= 0) && (state != m_castersRead))) { StaticDrawsChanged(); }
                            state = CasterStands;
                        } else if (state == CasterStands) {
                            state = m_castersRead;
                        }
                    }

                    var wasLive = ReadStagedSelection(row: index, record: record, box: out var before, glass: out var wasGlass);
                    var wasCaster = SdfMeshRegion.CastsShadow(record: record);
                    var posed = (sameRoster && (difference == SdfMeshDrawDifference.Matrix) && (index < m_stagedCount));
                    var mask = StageMeshDraw(draw: in draw, index: index, posed: posed, record: record, tally: ref tally);

                    NoteStagedRow(row: index, wasLive: wasLive, before: in before, wasGlass: wasGlass, wasCaster: wasCaster, draw: in draw, record: record);
                    // A grown region's new bytes are unknown to its host copy, so its zeros are not evidence of device
                    // zeros: initialize every word of a newly exposed record, including zero placement words.
                    if (index >= m_stagedCount) { mask = (ulong.MaxValue >> (64 - SdfMeshRecord.DrawWords)); }

                    changedWords += m_meshDrawDifferences.AddMask(mask, start: (index * SdfMeshRecord.DrawWords));
                    changedRecords++;

                    m_stagedDraws[index] = draw;
                    m_stagedOccupancies[index] = draws.Occupancy(row: index);
                } else if (difference == SdfMeshDrawDifference.Slot) {
                    m_stagedDraws[index] = draw;
                }
            }
        }
        // Every previous-matrix decision above reads the unmodified shadow. Only the exact packed-word delta is
        // copied and owed now; bounded run coarsening may include unchanged words already valid in records.
        if (m_meshDrawDifferences.Count > 0) { _ = m_meshRegion.WriteRuns(runs: m_meshDrawDifferences, words: records); }
        tally.Publish();
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.RecordsCompared, amount: comparedRecords);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.RecordsChanged, amount: changedRecords);
        SdfMeshHistory.ProcessWork.Add(kind: SdfMeshHistory.PackedWordsChanged, amount: changedWords);
        return changedRecords;
    }

    /// <summary>Gets or sets the demand points the store lands this residency's owed meshes by
    /// (<see cref="SdfMeshDemand"/>): the draws they see largest first, a draw no host box bounds (a skinned body)
    /// before any, and the list's order among equals; <see langword="null"/>, or one with no point, lands them in the
    /// list's order. Read under the store's gate, when a staging or a take orders the owed meshes.</summary>
    public SdfMeshDemand? MeshDemand { get; set; }

    // The frames and time the residency spent with a staged draw's mesh owed, the words owed now, and since when (a
    // Stopwatch timestamp; zero while it owes none).
    private long m_owedFrames;
    private long m_owedTicks;
    private long m_owedWords;
    private long m_owedSince;

    private ulong m_owedCountedUpload = ulong.MaxValue;

    // The uploads submitted when a staging last stopped owing words; zero while none has.
    private long m_owedClearedUpload;
    // The mesh region bytes the frame uploads staged: the latest upload's, the most one upload staged, and the sum.
    private long m_frameStagedLast;
    private long m_frameStagedMost;
    private long m_frameStagedTotal;
    // The one-based upload that staged the most.
    private long m_frameStagedMostUpload;

    // Counts the mesh region bytes one frame upload stages, before its flush.
    private void CountFrameMeshStaging(long bytes) {
        Volatile.Write(location: ref m_frameStagedLast, value: bytes);
        Volatile.Write(location: ref m_frameStagedTotal, value: (m_frameStagedTotal + bytes));
        if (bytes > m_frameStagedMost) {
            Volatile.Write(location: ref m_frameStagedMost, value: bytes);
            Volatile.Write(location: ref m_frameStagedMostUpload, value: ((long)(m_uploads + 1UL)));
        }
    }
    // One accounting door for the owed window. Count a prepared upload once even when a held frame is staged again;
    // completion and a row's last name leaving can close the window without another whole-list plan.
    private void AccountMeshOwed(bool frame = false) {
        var words = Volatile.Read(location: ref m_owedWords);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (words > 0) {
            if (frame && (m_owedCountedUpload != m_uploads)) { m_owedFrames++; m_owedCountedUpload = m_uploads; }
            if (m_owedSince == 0) { m_owedSince = now; }
        } else if (m_owedSince != 0) {
            m_owedTicks += (now - m_owedSince);
            m_owedSince = 0;
            Volatile.Write(location: ref m_owedClearedUpload, value: ((long)m_uploads));
        }
    }

    /// <summary>Returns whether the store still owes a mesh's words: it is placed and has not landed, or a draw names it
    /// and the store refuses it under its limit. Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh, a chunk of a chunked group's mesh being a mesh of its own.</param>
    /// <returns><see langword="true"/> while the store owes it.</returns>
    public bool OwesMesh(SdfMesh mesh) => m_meshStore.Owes(mesh: mesh);
    /// <summary>Returns whether a draw names a mesh the store refuses under its limit, so its draws wait out of the frame
    /// until a later plan fits it. Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh, a chunk of a chunked group's mesh being a mesh of its own.</param>
    /// <returns><see langword="true"/> while the store refuses it.</returns>
    public bool RefusesMesh(SdfMesh mesh) => m_meshStore.Refuses(mesh: mesh);
    /// <summary>Names the meshes the draws are about to name, in place of those this residency named ahead before
    /// (<see cref="SdfMeshRegionStore.ReplaceAhead"/>): each is placed beside the meshes the draws read and lands
    /// through the background work after the meshes a draw waits on, and the store keeps it until a draw names it or it
    /// leaves every residency's set. No draw record changes, so the staged frame stands. On the frame's thread, after the
    /// frame's draws are staged and before its upload.</summary>
    /// <param name="meshes">The meshes, a chunk of a chunked mesh being a mesh of its own.</param>
    /// <param name="products">How many of <paramref name="meshes"/>, from the first, replace a drawn mesh and wait
    /// beside it: the ones the store gives back first when its share of the pool falls
    /// (<see cref="SdfMeshRegionArena.SetLimit"/>). The rest keep their words.</param>
    /// <exception cref="ArgumentNullException"><paramref name="meshes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="products"/> is negative or more than the
    /// meshes.</exception>
    public void NameMeshesAhead(IReadOnlyList<SdfMesh> meshes, int products = 0) {
        ArgumentNullException.ThrowIfNull(argument: meshes);
        ArgumentOutOfRangeException.ThrowIfNegative(value: products);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(other: meshes.Count, value: products);
        if (m_disposed) { return; }
        m_meshStore.ReplaceAhead(client: this, held: m_meshesAhead, meshes: meshes, products: products);
    }
    /// <summary>Returns whether a mesh's words stand in the store where a draw naming it would read them
    /// (<see cref="SdfMeshRegionStore.Stands"/>). Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh, a chunk of a chunked mesh being a mesh of its own.</param>
    /// <returns><see langword="true"/> when a draw may name the mesh without waiting out of the frame.</returns>
    public bool MeshStands(SdfMesh mesh) => m_meshStore.Stands(mesh: mesh);
    /// <summary>Returns whether the store found no room for a mesh named ahead beside the meshes the draws read
    /// (<see cref="SdfMeshRegionStore.RefusesAhead"/>): it can land only in the memory the mesh it replaces gives up, so
    /// it takes the draws first. Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> when the mesh is named ahead and refused.</returns>
    public bool RefusesAhead(SdfMesh mesh) => m_meshStore.RefusesAhead(mesh: mesh);
    /// <summary>Describes the residency's landing: this residency's part of the bytes its draws name, the bytes of its
    /// draws' meshes the store still owes, the frames and seconds it spent owing them, the draw-record bytes the frame
    /// uploads staged (the latest, the most one staged and at which upload, and their sum), the uploads submitted and the
    /// one at which a staging last stopped owing, the draw records it holds room for, the rows the latest staging shed
    /// because the draw region could not grow and the stagings that shed any, and the meshes of its draws the
    /// store refuses; then the presentation's store (<see cref="SdfMeshRegionStore.Describe"/>), which every residency
    /// prints alike. Safe to read from any thread, the counts being a frame's.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeLanding() {
        var since = Volatile.Read(location: ref m_owedSince);
        var ticks = (Volatile.Read(location: ref m_owedTicks) + ((since == 0) ? 0 : (System.Diagnostics.Stopwatch.GetTimestamp() - since)));
        var seconds = (((double)ticks) / System.Diagnostics.Stopwatch.Frequency);

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"region-named={MeshRegionNamedBytes}B owed={(Volatile.Read(location: ref m_owedWords) * sizeof(uint))}B owed-frames={Volatile.Read(location: ref m_owedFrames)} owed-seconds={seconds:0.000}s owed-clock={((since == 0) ? "settled" : "running")} frame-staged-last={Volatile.Read(location: ref m_frameStagedLast)}B frame-staged-most={Volatile.Read(location: ref m_frameStagedMost)}B@upload{Volatile.Read(location: ref m_frameStagedMostUpload)} frame-staged-total={Volatile.Read(location: ref m_frameStagedTotal)}B uploads={m_uploads} owed-cleared-upload={Volatile.Read(location: ref m_owedClearedUpload)} draw-capacity={m_meshDrawCapacity} draws-shed={m_drawsShed} shed-stagings={m_drawShedStagings} | {m_meshStore.Describe()}");
    }
}
