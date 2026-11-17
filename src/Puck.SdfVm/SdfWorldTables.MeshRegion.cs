using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh region: the frame's mesh draws (SdfFrame.MeshDraws) in one GpuRegion, created like every other table
// (SdfWorldTables.Regions.cs) and copied with them in the upload. The region's arena (SdfMeshRegionArena) keeps each mesh
// at the words it was first placed at, so a frame whose draw list is the one last packed, at the revision and program
// composability last packed, packs nothing, and any other packs its draw records and only the meshes the arena newly
// placed: a body that moves rewrites its record's matrix, and a mesh that lands writes its own words, never the meshes
// already placed. Placed meshes land through the background GPU work (SdfWorldTables.MeshLanding.cs), those the views see
// largest first (MeshDemand), and a draw whose mesh is still owed waits out of the staged records until it lands, so a
// boot, a relocation or a body crossing into a world with all its meshes at once fills in over several frames from the
// cameras outward rather than staging every word in one upload. The region is created with the tables, one draw record
// long so every set that binds it binds a buffer, and grows to the need plus an eighth without waiting for the device, the
// next upload carrying the words it held on the GPU; a frame without draws keeps it. It never grows past its limit
// (MeshRegionLimitBytes), a share of the device's memory: a mesh the arena finds no room for under it is refused, its
// draws wait out of the staged records like an owed mesh's, and the bake coarsens what the views can spare until the
// list fits (SdfMeshPathBake.ObserveResidency). A mesh that replaces one a draw reads is named ahead of the draws
// (NameMeshesAhead) and lands beside the one it replaces, which the draws read until the new mesh stands
// (MeshStands), so the swap holds no draw out of a frame. A view's mesh pass reads the draws' triangles from it, and
// primary a mesh hit's material.
public sealed partial class SdfWorldTables {
    private readonly SdfMeshRegionArena m_meshArena;

    private long m_meshPlacementVersion;

    // Called only during a background admission on the frame thread. An owed geometry is never bound.
    internal bool TryProbeScene(SdfMeshIrradianceScene scene, out IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, out IGpuBuffer meshes, out long version) {
        placements = m_meshArena.Placements;
        meshes = m_meshRegion.Buffer(slot: m_currentSlot);
        version = m_meshPlacementVersion;
        return (BindsBackgroundWork && (m_meshRequiredUpload <= m_uploads) && scene.HasGpuPlacements(placements, m_meshesOwed.Set));
    }
    internal GpuRegion CreateProbeSceneRegion(int bytes, int slots = 1) => new(
        policy: GpuResidencyPolicy.Staged, memory: GpuResidency.RingMemory(profile: m_deviceContext.MemoryProfile),
        byteCount: bytes, slotCount: slots, buffers: m_gpu.BufferFactory, bindings: m_gpu.Bindings,
        recorder: m_gpu.Recorder, copyPipeline: m_regionCopyPipeline, name: NameOf("probe-scene"));

    private SdfMeshClaims? m_meshClaims;
    private bool m_meshTrimListening;
    // One while an account trim request waits: it is raised on whichever thread asked, and the arena and its claims
    // belong to the frame thread, which services it every frame (ServiceMemoryTrim), packed or not.
    private int m_meshTrimRequested;

    private void RequestMeshTrim() {
        if (m_disposed) { return; }
        if (Interlocked.Exchange(location1: ref m_meshTrimRequested, value: 1) == 0) { Puck.Hosting.Jobs.MemoryAccount.Shared.DeferTrim(); }
    }

    /// <summary>Releases, on the frame thread, the placement claims of meshes the latest draws no longer name when the
    /// process account has asked its caches to trim since the last call; otherwise does nothing.</summary>
    internal void ServiceMemoryTrim() {
        if (Interlocked.Exchange(location1: ref m_meshTrimRequested, value: 0) == 0) { return; }
        try {
            m_meshArena.TrimUnused();
            m_meshesOwed.RemoveWhere(match: mesh => !m_meshArena.Placements.ContainsKey(key: mesh));
            ForgetAllLanding();
            // The words a trim freed may hold a mesh the arena refused, so the next staging plans its list again.
            if (m_meshArena.Refused.Count > 0) { m_meshSource = null; }
            // The source order walks the placements, so a trim that forgot one rebuilds it.
            m_meshOrderStale = true;
        } finally {
            Puck.Hosting.Jobs.MemoryAccount.Shared.CompleteDeferredTrim();
        }
    }

    // Whether the arena has written its meshes into the region.
    private bool m_meshSectionWritten;
    // The one-based upload required by the current sourced geometry or buffer carry. Draw matrices and irradiance
    // never move it. Zero means no geometry has needed an upload yet.
    private ulong m_meshRequiredUpload;

    /// <summary>Gets whether the latest staged draws name a mesh whose words the region still owes: the mesh lands through
    /// the background GPU work, and its draws wait out of the staged records until it does. It turns false as the last
    /// such mesh lands, before a staging names its draws (<see cref="DrawsAwaitMeshes"/>). Read on the frame
    /// thread.</summary>
    public bool MeshesLanding => (Volatile.Read(location: ref m_owedWords) != 0);
    /// <summary>Gets whether the latest staging held a draw out of the staged records because its mesh had not landed, so
    /// the frame it staged renders without it. Read on the frame thread.</summary>
    public bool DrawsAwaitMeshes => ((m_meshSource is not null) && !m_stagedWhole);
    /// <summary>Gets whether the current staged mesh geometry owes no mesh words, the region refuses none of its meshes,
    /// and its required upload has completed. Pose, draw-record and irradiance uploads do not delay it. Read on the frame
    /// thread; never waits.</summary>
    public bool MeshesResident => (MeshResidencyPending() is null);
    /// <summary>Gets whether the current staged mesh geometry is still on its way to the region, and only frames that
    /// render the tables' views bring it: words the region owes, which land through background GPU work that binds only
    /// the World set of an upload after the latest pack (<see cref="BindsBackgroundWork"/>), or a required upload not yet
    /// submitted or complete. Unlike <see cref="MeshesResident"/> it ignores the meshes the region refuses under its
    /// limit, which no frame brings. Read on the frame thread; never waits.</summary>
    public bool MeshesArriving => (!m_disposed && ((Volatile.Read(location: ref m_owedWords) != 0) || !MeshUploadComplete));

    // Whether the upload the current sourced geometry requires has been submitted and completed.
    private bool MeshUploadComplete {
        get {
            var required = m_meshRequiredUpload;

            return ((required == 0UL) || ((required <= m_uploads) &&
                ((required <= m_completedUploads) || m_frameFences[((int)((required - 1UL) % FrameRingSize))].IsSignaled)));
        }
    }

    /// <summary>Names what keeps <see cref="MeshesResident"/> false: the owed words and meshes of the staged list, how many
    /// of those wait on a store restore, whether background work may bind the latest upload, the meshes the region
    /// refuses under its limit, and the required upload against the submitted and completed ones and its fence;
    /// <see langword="null"/> once resident. Read on the frame thread; never waits.</summary>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public string? MeshResidencyPending() {
        if (m_disposed) { return "the tables are disposed"; }
        var owed = Volatile.Read(location: ref m_owedWords);
        var required = m_meshRequiredUpload;

        if (owed != 0) {
            var restoring = 0;

            foreach (var mesh in m_activeOwed) { if (!mesh.Resident) { restoring++; } }
            return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler:
                $"owes {(owed * sizeof(uint))}B of {m_activeOwed.Count} staged meshes ({restoring} restoring from the store, {m_landing.Count} in landing) binds-background={BindsBackgroundWork} uploads={m_uploads} binding-revision={m_bindingRevision}/{m_uploadedBindingRevision} relocations={m_meshArena.Relocations} ({m_meshArena.RelocationReason}) moved={m_meshMoved} refused={m_meshArena.Refused.Count}");
        }
        if (m_meshArena.Refused.Count > 0) {
            return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler:
                $"the mesh region refuses {m_meshArena.Refused.Count} meshes: the staged list names {MeshRegionNamedBytes}B, and the region is limited to {MeshRegionLimitBytes}B");
        }
        if (MeshUploadComplete) { return null; }
        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler:
            $"requires upload {required}; submitted {m_uploads}, completed {m_completedUploads}{((required <= m_uploads) ? ", its fence unsignalled" : ", not yet submitted")}");
    }

    // Invalidate before staging words or replacing their buffer: until the next submission completes, the geometry
    // currently bound by these tables is not resident, even if an earlier slot's fence is already signaled.
    private void RequireMeshUpload() => m_meshRequiredUpload = checked((m_uploads + 1UL));

    // The draw list last planned and its revision, and whether its draws are all staged: a list whose meshes' words are
    // still owed stages again each frame as they land.
    private IReadOnlyList<SdfMeshDraw>? m_meshSource;
    private long m_meshSourceRevision;
    private bool m_meshStaged;
    // A consumed draw journal belongs to the exact staged roster, not just a revision number. Landing filters and
    // unknown/missing history go through ordinary planning before establishing a new cursor.
    private GpuChangedRows? m_drawChangesSource;
    private long m_drawChangesSerial;

    private readonly GpuUploadRuns m_owedDraws = new(capacity: GpuRegion.MaxCopyRuns);

    /// <summary>Gets the logical array payload retained by this consumer's changed-draw and transform-cut runs.</summary>
    public long DrawChangeScratchBytes => ((m_owedDraws.StorageBytes +
        m_meshTransformReseats.StorageBytes) + m_reseatedDraws.StorageBytes);

    // The placed meshes whose words the region still owes, and the draws of the planned list whose meshes are written.
    private readonly OwedMeshes m_meshesOwed;

    // The meshes a grown draw reserve moved, written by the next frame upload's flush: each holds a need until that upload
    // is submitted (ReleaseUploadNeeds).
    private readonly List<SdfMesh> m_meshesWithUpload = [];

    private void ReleaseUploadNeeds() {
        foreach (var mesh in m_meshesWithUpload) { mesh.Unneed(); }
        m_meshesWithUpload.Clear();
    }

    private readonly HashSet<SdfMesh> m_activeOwed = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMeshDraw> m_meshLanded = [];
    // A copy of each draw last staged, by index, and whether that staging staged its whole list (no draw waiting on an
    // owed mesh): what StageChangedDraws compares a list against.
    private SdfMeshDraw[] m_stagedDraws = [];

    private int m_stagedCount;
    private bool m_stagedWhole;

    private readonly GpuRegion m_meshRegion;

    // One more for every new draw list the region packs: the cadence signature folds it, so a frame whose draws moved
    // renders.
    private long m_meshRevision;

    /// <summary>Gets the revision of the staged draw records: one more for every staging that wrote a record. A target a
    /// pass rasterized holds hits that resolve through the records of the revision it was recorded under. Read on the
    /// frame thread.</summary>
    public long MeshRevision => m_meshRevision;

    // The cause the records the current staging packs count under, or null while it takes the draw journal, which splits
    // them between a hand-over and a pose (StagingTally).
    private SdfMeshStagingCause? m_stagingCause;
    private ulong m_meshRegionBytes;
    private ulong m_meshRegionUsedBytes;
    // The draws the frame being rendered rasterizes, from its staged draw list, and how many of them are glass.
    private uint m_meshDrawCount;
    private uint m_glassDrawCount;

    // The draw records as the next write sends them, and one mesh's words as a flush stages them.
    private uint[] m_meshDrawWords = [];
    private uint[] m_meshWords = [];
    private readonly GpuUploadRuns m_meshDrawDifferences = new(capacity: GpuRegion.MaxCopyRuns, mergeGap: GpuRegion.StagedMergeGapWords);

    // The region's source for its mesh section, and the placed meshes by their first word, sorted again after a plan
    // places or forgets one.
    private MeshSection? m_meshSection;

    private readonly List<(int Word, SdfMesh Mesh)> m_meshOrder = [];
    private bool m_meshOrderStale = true;
    // The zeros a flush stages into words no placed mesh holds.
    private static readonly uint[] ZeroWords = new uint[(1 << 16)];

    /// <summary>Gets the words the mesh region's draw records and meshes take for the draws the latest frame staged; zero
    /// before a frame draws a mesh.</summary>
    public int MeshRegionWords => m_meshArena.Words;

    /// <summary>The most bytes one mesh region can hold on any device, a whole number of words
    /// (<see cref="GpuRegion.MaxByteCount"/>): the ceiling of the limit its grant reserves for it
    /// (<see cref="MeshRegionLimitBytes"/>).</summary>
    public const ulong MostMeshRegionBytes = ((ulong)GpuRegion.MaxByteCount);
    /// <summary>The unit a grown mesh region's size is rounded up to: 16 MiB.</summary>
    public const ulong MeshRegionGranuleBytes = (16UL << 20);

    /// <summary>Gets the bytes the mesh region holds, which its buffers are allocated for: one draw record's until a frame
    /// needs more. Safe to read from any thread.</summary>
    public ulong MeshRegionBytes => Volatile.Read(location: ref m_meshRegionBytes);
    /// <summary>Gets the bytes of the mesh region the latest planned draw list uses: its draw records and the meshes it
    /// places, which <see cref="MeshRegionBytes"/> holds with room to grow. Safe to read from any thread.</summary>
    public ulong MeshRegionUsedBytes => Volatile.Read(location: ref m_meshRegionUsedBytes);

    // The residency's part of its presentation's selector: the reservation the region grows under, and the budget and
    // bias its bake holds its detail to.
    private readonly SdfResidencyGrant m_meshGrant;

    /// <summary>Gets the residency's grant from its presentation's selector, which its bake reads its budget and bias
    /// from.</summary>
    public SdfResidencyGrant Grant => m_meshGrant;
    /// <summary>Gets the most bytes the mesh region places, as its grant last reserved them before a plan
    /// (<see cref="SdfResidencyGrant.ReserveGrowth"/>): the buffer that stands when the pool has no room for a
    /// replacement beside it, and otherwise what the pool has room for. A mesh the latest list names that finds no room
    /// under it is refused, and its draws wait until a later list fits.</summary>
    public ulong MeshRegionLimitBytes => (((ulong)m_meshArena.LimitWords) * sizeof(uint));

    // Reserves room for the region to grow before a plan places meshes, and holds the arena to what was reserved, so no
    // mesh is placed in words the region could not be grown to hold.
    private void PrepareMeshRegionLimit() => m_meshArena.SetLimit(limitBytes: ((ulong)m_meshGrant.ReserveGrowth(capacityBytes: m_meshRegion.ByteCount, mostBytes: ((long)MostMeshRegionBytes))));
    // A plan forgot these meshes: their words are free for the next mesh they hold, so they owe none and their landing
    // starts again should a list place them later.
    private void ForgetArenaMeshes() {
        foreach (var mesh in m_meshArena.Forgotten) {
            ForgetLanding(mesh: mesh);
            _ = m_meshesOwed.Remove(mesh: mesh);
        }
        m_meshForgotten += m_meshArena.Forgotten.Count;
    }

    // A region buffer a growth or a shrink replaced, which leaves the grant's reservation when its readers finish and
    // it is released.
    private sealed class ReplacedRegion(IDisposable replaced, long bytes, SdfResidencyGrant grant) : IDisposable {
        public void Dispose() {
            replaced.Dispose();
            grant.Released(bytes: bytes);
        }
    }

    /// <summary>Gets the bytes the latest planned draw list asks of the mesh region: its reserved draw records and every
    /// mesh it names, each once, placed or refused. Past <see cref="MeshRegionLimitBytes"/> the list cannot be resident
    /// whole. Safe to read from any thread.</summary>
    public ulong MeshRegionNamedBytes => Volatile.Read(location: ref m_meshRegionNamedBytes);
    /// <summary>Gets how many meshes the latest planned draw list names that the mesh region refuses under its limit.
    /// Read on the frame thread.</summary>
    public int MeshRegionRefused => m_meshArena.Refused.Count;

    private ulong m_meshRegionNamedBytes;
    // The meshes plans refused and forgot, over the tables' life.
    private long m_meshRefusals;
    private long m_meshForgotten;

    /// <summary>Returns the size a mesh region grows to when its meshes need <paramref name="needBytes"/>: the need plus an
    /// eighth, rounded up to <see cref="MeshRegionGranuleBytes"/> and capped at <paramref name="mostBytes"/>. The size is
    /// never below the need and is a whole number of words.</summary>
    /// <param name="needBytes">The bytes the planned draws and meshes use; at most <paramref name="mostBytes"/>.</param>
    /// <param name="mostBytes">The most bytes the region may hold, a whole number of words; at most
    /// <see cref="MostMeshRegionBytes"/>.</param>
    /// <returns>The region's new size in bytes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="needBytes"/> is past <paramref name="mostBytes"/>.</exception>
    public static ulong MeshRegionGrownBytes(ulong needBytes, ulong mostBytes = MostMeshRegionBytes) {
        if (needBytes > mostBytes) {
            throw new InvalidOperationException(message: $"The baked meshes need {needBytes} bytes of mesh region, past the {mostBytes} the region may hold.");
        }

        return ((ulong)GpuCapacity.Headroom(ceiling: ((long)mostBytes), divisor: 8, granule: ((long)MeshRegionGranuleBytes), required: ((long)needBytes)));
    }

    /// <summary>Gets the draws the latest frame's mesh passes rasterize in each view, the glass draws among them.</summary>
    public uint MeshDrawCount => m_meshDrawCount;
    /// <summary>Gets how many of <see cref="MeshDrawCount"/> are glass (<see cref="SdfMeshDraw.Glass"/>): the glass pass
    /// draws them, and the mesh pass and the shadow cascades draw the rest.</summary>
    public uint GlassDrawCount => m_glassDrawCount;
    /// <summary>Gets the opaque draws the latest frame's mesh pass and shadow cascades rasterize.</summary>
    public uint OpaqueDrawCount => (m_meshDrawCount - m_glassDrawCount);

    // The staged draws that are glass, by index, as of the mesh revision they were collected at.
    private readonly List<int> m_glassIndices = [];
    private long m_glassIndicesRevision = -1;

    /// <summary>Returns whether the glass pass would draw any glass draw of the staged list under a cull: the test the
    /// pass's own selection applies to each draw's host box (<see cref="SdfMeshCull.Keeps"/>), so a draw no host box
    /// bounds counts as drawn.</summary>
    /// <param name="cull">The glass pass's cull.</param>
    /// <returns><see langword="false"/> when the pass keeps no glass draw, so it rasterizes nothing.</returns>
    public bool GlassMaySee(in SdfMeshCull cull) {
        if (m_glassDrawCount == 0) {
            return false;
        }
        if (m_glassIndicesRevision != m_meshRevision) {
            m_glassIndices.Clear();
            for (var index = 0; (index < m_stagedCount); index++) {
                if (m_stagedDraws[index].Glass) { m_glassIndices.Add(item: index); }
            }
            m_glassIndicesRevision = m_meshRevision;
        }
        foreach (var index in m_glassIndices) {
            SdfMeshBox? box = (SdfMeshBox.TryOf(draw: in m_stagedDraws[index], box: out var bounded) ? bounded : null);

            if (cull.Keeps(box: in box)) {
                return true;
            }
        }

        return false;
    }

    // Packs new draws or a new revision, growing the region when the list needs more
    // bytes; the upload sends the slot what it owes.
    private void StageMeshRegion(SdfFrame frame) {
        var draws = frame.MeshDraws;
        var revision = frame.MeshDrawsRevision;

        if (!m_meshTrimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested += RequestMeshTrim; m_meshTrimListening = true; }
        ServiceMemoryTrim();
        if (draws.Count == 0) { m_meshHistory.RetireAfter(count: 0); }
        SdfMeshHistory.ProcessWork.Raise(kind: SdfMeshHistory.PeakRowsExtent, value: draws.Count);
        PrepareMeshTransformCuts(frame: frame);
        if (TryStageDrawChanges(frame: frame, refused: out var refused)) { CommitMeshTransformCuts(frame: frame); return; }
        // Every record the rest of this staging packs, on either path, counts under the one cause it took the whole
        // list for.
        m_stagingCause = refused;
        // Equal-pose cuts need staging too. This also forces the ordinary landing-filter path to observe its actual
        // staged roster, where producer draw indices cannot be used.
        if (m_reseatedDraws.Count > 0) { m_meshStaged = false; }
        // The staged list's meshes hold live claims; the arena's placement claims keep every placed mesh's storage, the
        // ones the list stopped naming included, until the arena forgets them.
        if (!ReferenceEquals(objA: draws, objB: m_meshSource) || (revision != m_meshSourceRevision)) {
            (m_meshClaims ??= new SdfMeshClaims()).Update(draws: draws);
            SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingClaimRecords, draws.Count);
        }
        StageMeshRegionCore(draws: draws, revision: revision);
        // Publish the cursor only after the whole roster has staged successfully. No landing-filtered index can be
        // interpreted as an index into the producer's list.
        m_drawChangesSource = ((m_meshStaged && m_stagedWhole) ? frame.MeshDrawChanges : null);
        m_drawChangesSerial = frame.MeshDrawChangesSerial;
        CommitMeshTransformCuts(frame: frame);
    }

    /// <summary>Stages again the draws of the frame the tables last packed when meshes its draws name have landed since,
    /// so a frame that stands draws them; does nothing otherwise. Staging the same frame again consumes no transform or
    /// draw change a staging already consumed.</summary>
    /// <param name="frame">The frame the tables last packed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public void StageLandedMeshes(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        if (m_disposed || m_meshStaged || !m_meshLandedSinceStaging || !ReferenceEquals(objA: frame.MeshDraws, objB: m_meshSource) || (frame.MeshDrawsRevision != m_meshSourceRevision)) { return; }
        StageMeshRegion(frame: frame);
    }

    // The cause a staging that does not take the frame's journal packs its records under: the cause the frame states for
    // the journal entry this staging is the first to consume, then a landing's refusal, then a lost cursor.
    private SdfMeshStagingCause RefusalOf(SdfFrame frame) {
        // Serials belong to one producer. A replacement list can start at the serial the previous list ended at.
        if ((frame.MeshDrawChangesCause is { } stated) &&
            (!ReferenceEquals(objA: frame.MeshDraws, objB: m_meshSource) || (frame.MeshDrawChangesSerial != m_drawChangesSerial))) { return stated; }
        if ((frame.MeshDrawChanges is null) || (m_meshSource is null)) { return SdfMeshStagingCause.Cursor; }
        return ((!m_meshStaged || !m_stagedWhole || (m_owedWords != 0) || (frame.MeshDraws.Count != m_stagedCount))
            ? SdfMeshStagingCause.Landing
            : SdfMeshStagingCause.Cursor);
    }
    private bool TryStageDrawChanges(SdfFrame frame, out SdfMeshStagingCause refused) {
        var changes = frame.MeshDrawChanges;
        var draws = frame.MeshDraws;

        refused = RefusalOf(frame: frame);
        if ((changes is null) || !ReferenceEquals(objA: changes, objB: m_drawChangesSource) || !ReferenceEquals(objA: draws, objB: m_meshSource) ||
            !m_meshStaged || !m_stagedWhole || (m_owedWords != 0) || (draws.Count != m_stagedCount) ||
            ((frame.MeshDrawsRevision != m_meshSourceRevision) && (frame.MeshDrawChangesSerial <= m_drawChangesSerial)) ||
            !changes.TryCollect(since: m_drawChangesSerial, through: frame.MeshDrawChangesSerial, into: m_owedDraws)) { return false; }

        CopyRuns(from: m_reseatedDraws, to: m_owedDraws);
        var checkedMeshes = 0;
        var renamed = 0;

        for (var run = 0; (run < m_owedDraws.Count); run++) {
            var end = Math.Min(val1: draws.Count, val2: checked((m_owedDraws.Start(index: run) + m_owedDraws.Length(index: run))));

            for (var index = m_owedDraws.Start(index: run); (index < end); index++) {
                var mesh = draws[index].Mesh;

                checkedMeshes++;
                if (ReferenceEquals(objA: mesh, objB: m_stagedDraws[index].Mesh)) { continue; }
                // A row takes another mesh here only when that mesh's words already stand in the region (one that
                // landed ahead of the draws): any other change of a row's mesh is planned and lands.
                if (!MeshStands(mesh: mesh)) {
                    SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingMeshChecks, checkedMeshes);
                    refused = SdfMeshStagingCause.Landing;
                    return false;
                }
                renamed++;
            }
        }
        SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingMeshChecks, checkedMeshes);
        // The arena and the staged list's claims follow each row that names another standing mesh, as a plan of the
        // list would leave them, so the rows alone are staged: no plan, no walk of the claims, no landing.
        for (var run = 0; ((renamed > 0) && (run < m_owedDraws.Count)); run++) {
            var end = Math.Min(val1: draws.Count, val2: checked((m_owedDraws.Start(index: run) + m_owedDraws.Length(index: run))));

            for (var index = m_owedDraws.Start(index: run); (index < end); index++) {
                var mesh = draws[index].Mesh;
                var staged = m_stagedDraws[index].Mesh;

                if (ReferenceEquals(objA: mesh, objB: staged)) { continue; }
                m_meshArena.Rename(index: index, mesh: mesh);
                m_meshClaims?.Move(from: staged, to: mesh);
            }
        }
        if (renamed > 0) {
            Volatile.Write(
                location: ref m_meshRegionNamedBytes,
                value: ((((ulong)m_meshArena.DrawCapacity) * SdfMeshRegion.DrawBytes) + (((ulong)m_meshArena.NamedWords) * sizeof(uint)))
            );
        }
        // Every mesh is held at its arena address. Pose, metadata and such a renamed row need neither a new placement
        // plan nor a walk of all mesh claims. Coarsened journal runs may include unchanged records; compare them once.
        m_stagingCause = null;
        if ((draws.Count > 0) && (StageChangedDraws(candidates: m_owedDraws, draws: draws) > 0)) { m_meshRevision++; }
        m_meshSourceRevision = frame.MeshDrawsRevision;
        m_drawChangesSerial = frame.MeshDrawChangesSerial;
        return true;
    }
    private void StageMeshRegionCore(IReadOnlyList<SdfMeshDraw> draws, long revision) {
        if (
            !ReferenceEquals(
                objA: draws,
                objB: m_meshSource
            ) ||
            (revision != m_meshSourceRevision)
        ) {
            PrepareMeshRegionLimit();
            m_meshArena.Plan(draws: draws);
            m_meshGrant.Floor(bytes: (((long)m_meshArena.DrawCapacity) * SdfMeshRegion.DrawBytes));
            if (!m_meshArena.Unchanged) { m_meshPlacementVersion++; }
            SdfMeshHistory.ProcessWork.Add(SdfMeshHistory.StagingPlanRecords, draws.Count);
            SdfMeshHistory.ProcessWork.Raise(kind: SdfMeshHistory.PeakRowsPlanned, value: draws.Count);
            m_meshOrderStale |= !m_meshArena.Unchanged;
            Volatile.Write(
                location: ref m_meshRegionUsedBytes,
                value: (((ulong)m_meshArena.Words) * sizeof(uint))
            );
            Volatile.Write(
                location: ref m_meshRegionNamedBytes,
                value: ((((ulong)m_meshArena.DrawCapacity) * SdfMeshRegion.DrawBytes) + (((ulong)m_meshArena.NamedWords) * sizeof(uint)))
            );
            ForgetArenaMeshes();

            if (m_meshArena.Words > 0) {
                if (m_meshArena.Relocated) {
                    FitMeshRegion(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                }
                EnsureMeshRegionCapacity(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                ShadowMeshRecords();
                // Every mesh is owed again when the arena moved them or none was written yet, since no mesh's words
                // stand where its draw reads them, and lands through the background work. Otherwise the meshes a grown
                // draw reserve moved are written in this frame's upload, which carries the records naming them, since
                // their draws were drawn from the words the reserve took; the rest this list placed are owed.
                if (m_meshArena.Relocated || !m_meshSectionWritten) {
                    m_meshesOwed.ReplaceWith(meshes: m_meshArena.Placements.Keys);
                    ForgetAllLanding();
                } else {
                    m_meshesOwed.UnionWith(meshes: m_meshArena.Placed);
                    foreach (var mesh in m_meshArena.Moved) {
                        m_meshMoved++;
                        ForgetLanding(mesh: mesh);
                        // A moved mesh no free run held is refused: it owes nothing until a plan places it.
                        if (!m_meshArena.Placements.TryGetValue(key: mesh, value: out var placement)) {
                            _ = m_meshesOwed.Remove(mesh: mesh);
                            continue;
                        }
                        // A moved mesh whose normals and levels are still coming back from the store stays owed and
                        // lands through the background work, its draws held out until it does.
                        if (!mesh.Resident) { continue; }
                        mesh.Need();
                        m_meshesWithUpload.Add(item: mesh);
                        _ = m_meshesOwed.Remove(mesh: mesh);
                        WriteMeshWords(mesh: mesh, placement: placement);
                    }
                }
                m_meshSectionWritten = true;
            }
            if (m_meshArena.Words == 0) { m_meshGrant.Kept(); }
            if (!m_meshArena.Unchanged) { m_meshRefusals += m_meshArena.Refused.Count; }
            // An owed mesh this list stopped naming lets its need go, and one it names again takes it back.
            if (!m_meshArena.Unchanged) { m_meshesOwed.Sync(); }

            m_meshSource = draws;
            m_meshSourceRevision = revision;
            m_meshStaged = false;
        }

        if (m_meshStaged) {
            return;
        }

        m_meshLandedSinceStaging = false;
        OrderLanding(draws: draws);
        CountOwed(draws: draws);

        // A draw whose mesh's words are still owed, or whose mesh the region refuses, waits for them: the staged list
        // holds the draws whose meshes are written, and a landing or a later plan stages the rest.
        var staged = draws;

        if ((m_meshesOwed.Count > 0) || (m_meshArena.Refused.Count > 0)) {
            m_meshLanded.Clear();
            foreach (var draw in draws) {
                if (!m_meshesOwed.Contains(mesh: draw.Mesh) && !m_meshArena.Refused.Contains(item: draw.Mesh)) { m_meshLanded.Add(item: draw); }
            }
            if (m_meshLanded.Count != draws.Count) { staged = m_meshLanded; }
        }

        // While existing mesh addresses stand, a same-sized list owes only its changed draws, including draws
        // that swapped meshes. Every other record, box and motion row stands as it was staged.
        if (
            !m_meshArena.Relocated &&
            (m_meshArena.Moved.Count == 0) &&
            m_stagedWhole &&
            ReferenceEquals(objA: staged, objB: draws) &&
            (staged.Count == m_stagedCount) &&
            (m_meshArena.Words > 0)
        ) {
            if (StageChangedDraws(draws: staged) > 0) { m_meshRevision++; }
            m_meshStaged = true;

            return;
        }

        var drawWords = (staged.Count * SdfMeshRecord.DrawWords);

        if (drawWords > m_meshDrawWords.Length) {
            m_meshDrawWords = new uint[drawWords];
        }

        var records = m_meshDrawWords.AsSpan(length: drawWords, start: 0);

        StageMeshMotion(
            draws: staged,
            words: records
        );

        if (m_meshArena.Words > 0) {
            _ = m_meshRegion.Write(bytes: MemoryMarshal.AsBytes(span: records), offset: 0);
        }

        m_meshDrawCount = ((uint)staged.Count);
        m_glassDrawCount = 0;
        for (var index = 0; (index < staged.Count); index++) {
            var draw = staged[index];

            if (draw.Glass) { m_glassDrawCount++; }
        }
        if (m_stagedDraws.Length < staged.Count) {
            m_stagedDraws = new SdfMeshDraw[Math.Max(val1: staged.Count, val2: (2 * m_stagedDraws.Length))];
        }
        for (var index = 0; (index < staged.Count); index++) {
            m_stagedDraws[index] = staged[index];
        }
        Array.Clear(array: m_stagedDraws, index: staged.Count, length: (m_stagedCount - Math.Min(val1: m_stagedCount, val2: staged.Count)));
        m_stagedCount = staged.Count;
        m_meshRevision++;
        // The records stand until a mesh lands (CompleteLanding), a trim frees words for a refused one
        // (ServiceMemoryTrim) or a list is planned, each of which stages again; a frame between them stages nothing.
        m_meshStaged = true;
        m_stagedWhole = ReferenceEquals(objA: staged, objB: draws);
    }
    // Stages changed draws at their indices while existing mesh addresses stand: each changed record, box and motion
    // row, and the region's words for each run of them.
    private int StageChangedDraws(IReadOnlyList<SdfMeshDraw> draws, GpuUploadRuns? candidates = null) {
        var records = m_meshDrawWords.AsSpan(length: (draws.Count * SdfMeshRecord.DrawWords), start: 0);
        // The records as the region last held them, which a moved matrix is compared against before it is written.
        var old = m_meshRegion.ContentsAt(
            length: ((int)Math.Min(val1: m_meshRegion.ByteCount, val2: (((long)draws.Count) * SdfMeshRegion.DrawBytes))),
            offset: 0
        );

        m_meshDrawDifferences.Clear();
        var changedRecords = 0;
        var changedWords = 0L;
        var comparedRecords = 0;
        var tally = new StagingTally(cause: m_stagingCause);

        for (var run = 0; (run < (candidates?.Count ?? 1)); run++) {
            var start = (candidates?.Start(index: run) ?? 0);
            var end = ((candidates is null) ? draws.Count : Math.Min(val1: draws.Count, val2: checked((start + candidates.Length(index: run)))));

            for (var index = start; (index < end); index++) {
                var draw = draws[index];

                comparedRecords++;

                if (!draw.SameStagedContent(other: in m_stagedDraws[index]) ||
                    (m_reseatedDraws.Contains(value: index) && MeshTransformWasReseated(draw: in draw))) {
                    var record = records.Slice(length: SdfMeshRecord.DrawWords, start: (index * SdfMeshRecord.DrawWords));

                    var mask = StageMeshDraw(draw: in draw, incremental: true, index: index, old: old, record: record, tally: ref tally);

                    changedWords += m_meshDrawDifferences.AddMask(mask, start: (index * SdfMeshRecord.DrawWords));
                    changedRecords++;
                    if (draw.Glass != m_stagedDraws[index].Glass) {
                        m_glassDrawCount = (draw.Glass ? (m_glassDrawCount + 1) : (m_glassDrawCount - 1));
                    }
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

    /// <summary>Gets or sets the demand points the mesh region lands owed meshes by (<see cref="SdfMeshDemand"/>): the
    /// draws they see largest first, a draw no host box bounds (a skinned body) before any, and the list's order among
    /// equals; <see langword="null"/>, or one with no point, lands them in the list's order. Read on the frame's
    /// thread, when a staging orders the owed meshes.</summary>
    public SdfMeshDemand? MeshDemand { get; set; }

    // The draws naming owed meshes by priority, reused frame to frame.
    private readonly List<(float Priority, int Index)> m_owedOrder = [];
    // Higher priority first, then the list's order.
    private static readonly Comparison<(float Priority, int Index)> ByPriority = static (left, right) =>
        ((right.Priority.CompareTo(value: left.Priority) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index));

    // Owes one placed mesh's words to this frame's upload: the region keeps no copy of the mesh section, and the upload's
    // flush stages the words from the mesh itself (WriteSourcedMeshWords). Only a mesh a grown draw reserve moved is
    // written so; every other lands through the background work. The byte offset is a long: a mesh placed past 512 M
    // words starts past 2 GiB.
    private void WriteMeshWords(SdfMesh mesh, SdfMeshRegionMesh placement) {
        var words = SdfMeshRegion.WordsOf(mesh: mesh);

        RequireMeshUpload();
        m_meshWordsWritten += words;
        m_meshRegion.OweSourced(
            length: (((long)words) * sizeof(uint)),
            offset: (((long)placement.VertexWord) * sizeof(uint))
        );
    }

    // The mesh words the region has written, the frames and time it spent owing words, the words it owes now, and since
    // when it has owed them (a Stopwatch timestamp; zero while it owes none).
    private long m_meshWordsWritten;
    // The meshes a grown draw reserve moved, over the tables' life.
    private long m_meshMoved;
    private long m_owedFrames;
    private long m_owedTicks;
    private long m_owedWords;
    private long m_owedSince;
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
    // Counts a staging that still owes meshes, and the time from the first such staging to the one that owes none.
    private void CountOwed(IReadOnlyList<SdfMeshDraw> draws) {
        var words = 0L;

        m_activeOwed.Clear();
        // Retained placements can be absent from the current roster. They still need an upload if reused, but they
        // neither block this roster's residency nor disable its changed-draw fast path. Count a shared mesh once.
        foreach (var draw in draws) {
            var mesh = draw.Mesh;

            if (m_meshesOwed.Contains(mesh: mesh) && m_activeOwed.Add(item: mesh)) { words += SdfMeshRegion.WordsOf(mesh: mesh); }
        }
        Volatile.Write(location: ref m_owedWords, value: words);

        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (words > 0) {
            m_owedFrames++;
            if (m_owedSince == 0) { m_owedSince = now; }
        } else if (m_owedSince != 0) {
            m_owedTicks += (now - m_owedSince);
            m_owedSince = 0;
            Volatile.Write(location: ref m_owedClearedUpload, value: ((long)m_uploads));
        }
    }

    /// <summary>Returns whether the mesh region still owes a mesh's words: the latest staged draw list placed it and its
    /// draws wait out of the staged records until a frame writes them, or the list names it and the region refuses it
    /// under its limit. Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh, a chunk of a chunked group's mesh being a mesh of its own.</param>
    /// <returns><see langword="true"/> while the region owes it.</returns>
    public bool OwesMesh(SdfMesh mesh) => (m_meshArena.Refused.Contains(item: mesh) || (m_meshArena.Placements.ContainsKey(key: mesh) && m_meshesOwed.Contains(mesh: mesh)));
    /// <summary>Names the meshes the draws are about to name, in place of those named ahead before
    /// (<see cref="SdfMeshRegionArena.PlanAhead"/>): each is placed beside the meshes the draws read and lands through
    /// the background work after the meshes a draw waits on, and the region keeps it until a draw list names it or it
    /// leaves the set. No draw record changes, so the staged frame stands. On the frame's thread, after the frame's
    /// draws are staged and before its upload.</summary>
    /// <param name="meshes">The meshes, a chunk of a chunked mesh being a mesh of its own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="meshes"/> is <see langword="null"/>.</exception>
    public void NameMeshesAhead(IReadOnlyList<SdfMesh> meshes) {
        ArgumentNullException.ThrowIfNull(argument: meshes);
        if (m_disposed) { return; }
        PrepareMeshRegionLimit();
        m_meshArena.PlanAhead(meshes: meshes);
        if ((m_meshArena.Placed.Count > 0) || (m_meshArena.Forgotten.Count > 0)) {
            m_meshPlacementVersion++;
            m_meshOrderStale = true;
            ForgetArenaMeshes();
            if (m_meshArena.Words > 0) {
                EnsureMeshRegionCapacity(bytes: (((ulong)m_meshArena.Words) * sizeof(uint)));
                ShadowMeshRecords();
            }
            Volatile.Write(location: ref m_meshRegionUsedBytes, value: (((ulong)m_meshArena.Words) * sizeof(uint)));
            m_meshesOwed.UnionWith(meshes: m_meshArena.Placed);
        } else {
            m_meshGrant.Kept();
        }
        // A mesh that left the set unnamed lets its need go, and one named again takes it back and its place in line.
        m_meshesOwed.Sync();
        OrderLandingAhead();
    }
    /// <summary>Returns whether a mesh's words stand in the mesh region where a draw naming it would read them: the
    /// arena holds it and the region owes none of its words. Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh, a chunk of a chunked mesh being a mesh of its own.</param>
    /// <returns><see langword="true"/> when a draw may name the mesh without waiting out of the frame.</returns>
    public bool MeshStands(SdfMesh mesh) => (m_meshArena.Placements.ContainsKey(key: mesh) && !m_meshesOwed.Contains(mesh: mesh));
    /// <summary>Returns whether the region found no room for a mesh named ahead beside the meshes the draws read
    /// (<see cref="SdfMeshRegionArena.AheadRefused"/>): it can land only in the words the mesh it replaces gives up.
    /// Read on the frame's thread.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> when the mesh is named ahead and refused.</returns>
    public bool RefusesAhead(SdfMesh mesh) => m_meshArena.AheadRefused.Contains(item: mesh);
    /// <summary>Describes the mesh region's landing: the bytes it holds and the latest list uses, the mesh bytes written
    /// into it, the bytes it still owes, the frames and seconds it spent owing them, the mesh region bytes the frame
    /// uploads staged (the latest, the most one staged and at which upload, and their sum), the uploads submitted and the
    /// one at which a staging last stopped owing, the arena's relocations and the latest one's reason, the meshes grown
    /// draw reserves moved, the region's limit, the bytes the latest list names, the meshes it refuses now and those
    /// plans refused and forgot in all, and the background landing jobs, their bytes and the most one job may carry.
    /// Safe to read from any thread, the counts being a frame's.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeLanding() {
        var since = Volatile.Read(location: ref m_owedSince);
        var ticks = (Volatile.Read(location: ref m_owedTicks) + ((since == 0) ? 0 : (System.Diagnostics.Stopwatch.GetTimestamp() - since)));
        var seconds = (((double)ticks) / System.Diagnostics.Stopwatch.Frequency);
        var written = (Volatile.Read(location: ref m_meshWordsWritten) * sizeof(uint));

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"region-held={MeshRegionBytes}B region-used={MeshRegionUsedBytes}B written={written}B owed={(Volatile.Read(location: ref m_owedWords) * sizeof(uint))}B owed-frames={Volatile.Read(location: ref m_owedFrames)} owed-seconds={seconds:0.000}s frame-staged-last={Volatile.Read(location: ref m_frameStagedLast)}B frame-staged-most={Volatile.Read(location: ref m_frameStagedMost)}B@upload{Volatile.Read(location: ref m_frameStagedMostUpload)} frame-staged-total={Volatile.Read(location: ref m_frameStagedTotal)}B uploads={m_uploads} owed-cleared-upload={Volatile.Read(location: ref m_owedClearedUpload)} relocations={m_meshArena.Relocations} relocation=\"{m_meshArena.RelocationReason}\" moved={m_meshMoved} region-limit={MeshRegionLimitBytes}B region-named={MeshRegionNamedBytes}B refused={m_meshArena.Refused.Count} ahead={m_meshArena.Ahead.Count} ahead-refused={m_meshArena.AheadRefused.Count} refusals={Volatile.Read(location: ref m_meshRefusals)} forgotten={Volatile.Read(location: ref m_meshForgotten)} {DescribeLandingJobs()}");
    }

    // The mesh whose words the mesh scratch holds, which a landing slicing it over several jobs reads again.
    private SdfMesh? m_meshWordsMesh;

    // One placed mesh's words, from its first vertex word, in the mesh scratch, written there unless it holds them. A
    // mesh's words relative to its first vertex word follow from the mesh alone. The words of a mesh not in the scratch
    // are read through a hold (SdfMesh.Hold), the one door to its normals and levels: a region copy asks for whatever
    // words its runs cover, and a run coarsened over a landed mesh whose parts were let go restores them first. A mesh
    // the store could not restore writes zeros; its groups bake again (SdfMeshLibrary.Lose).
    private ReadOnlySpan<uint> MeshWordsOf(SdfMesh mesh, SdfMeshRegionMesh placement) {
        var words = SdfMeshRegion.WordsOf(mesh: mesh);

        if (ReferenceEquals(objA: mesh, objB: m_meshWordsMesh)) { return m_meshWords.AsSpan(length: words, start: 0); }

        using var held = mesh.Hold();

        if (!held.Readable) { return ((words <= ZeroWords.Length) ? ZeroWords.AsSpan(length: words, start: 0) : new uint[words]); }
        if (words > m_meshWords.Length) {
            m_meshWords = new uint[Math.Max(val1: words, val2: (2 * m_meshWords.Length))];
        }

        var destination = m_meshWords.AsSpan(length: words, start: 0);

        SdfMeshRegion.WriteMesh(
            destination: destination,
            held: in held,
            placement: new SdfMeshRegionMesh(
                IndexCount: placement.IndexCount,
                IndexWord: (placement.IndexWord - placement.VertexWord),
                MaterialWord: (placement.MaterialWord - placement.VertexWord),
                VertexWord: 0,
                SkinWord: ((placement.SkinWord == 0) ? 0 : (placement.SkinWord - placement.VertexWord))
            )
        );
        m_meshWordsMesh = mesh;

        return destination;
    }
    // Hands the mesh region's words past the draw reserve to the meshes the arena places there, once the region holds the
    // planned words: the host copy keeps the draw records alone.
    private void ShadowMeshRecords() =>
        m_meshRegion.Shadow(
            shadowBytes: Math.Clamp(
                max: m_meshRegion.ByteCount,
                min: SdfMeshRegion.DrawBytes,
                value: (((long)m_meshArena.DrawCapacity) * SdfMeshRegion.DrawBytes)
            ),
            source: (m_meshSection ??= new MeshSection(tables: this))
        );
    // Writes the mesh region's words [word, word + count), past the draw reserve, into a staging buffer: each placed mesh's
    // words where the arena placed it, and zeros in the words no placed mesh holds, which no draw reads.
    private void WriteSourcedMeshWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
        if (m_meshOrderStale) {
            m_meshOrder.Clear();
            foreach (var (mesh, placement) in m_meshArena.Placements) { m_meshOrder.Add(item: (placement.VertexWord, mesh)); }
            m_meshOrder.Sort(comparison: static (left, right) => left.Word.CompareTo(value: right.Word));
            m_meshOrderStale = false;
        }

        var end = (((long)word) + count);
        var cursor = ((long)word);
        var low = 0;
        var high = m_meshOrder.Count;

        // The first placed mesh that ends past word.
        while (low < high) {
            var middle = ((low + high) >>> 1);

            var (start, mesh) = m_meshOrder[middle];

            if ((((long)start) + SdfMeshRegion.WordsOf(mesh: mesh)) <= word) {
                low = (middle + 1);
            } else {
                high = middle;
            }
        }
        for (var index = low; ((index < m_meshOrder.Count) && (m_meshOrder[index].Word < end)); index++) {
            var (start, mesh) = m_meshOrder[index];
            var words = MeshWordsOf(mesh: mesh, placement: m_meshArena.Placements[mesh]);
            var first = Math.Max(val1: cursor, val2: start);
            var last = Math.Min(val1: end, val2: (((long)start) + words.Length));

            WriteZeroWords(buffer: buffer, count: (first - cursor), destination: (destinationOffsetBytes + ((ulong)((cursor - word) * sizeof(uint)))));
            buffer.Write<uint>(
                data: words.Slice(length: ((int)(last - first)), start: ((int)(first - start))),
                destinationOffsetBytes: (destinationOffsetBytes + ((ulong)((first - word) * sizeof(uint))))
            );
            cursor = last;
        }
        WriteZeroWords(buffer: buffer, count: (end - cursor), destination: (destinationOffsetBytes + ((ulong)((cursor - word) * sizeof(uint)))));
    }
    private static void WriteZeroWords(IGpuStorageBuffer buffer, long count, ulong destination) {
        while (count > 0) {
            var piece = ((int)Math.Min(val1: count, val2: ZeroWords.Length));

            buffer.Write<uint>(data: ZeroWords.AsSpan(length: piece, start: 0), destinationOffsetBytes: destination);
            destination += (((ulong)piece) * sizeof(uint));
            count -= piece;
        }
    }

    // The mesh section's words as the region's source sees them.
    private sealed class MeshSection(SdfWorldTables tables) : IGpuRegionSource {
        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) =>
            tables.WriteSourcedMeshWords(buffer: buffer, count: count, destinationOffsetBytes: destinationOffsetBytes, word: word);
    }

    // Grows the region to MeshRegionGrownBytes of the need, refusing by name past what a region holds, without waiting for
    // the device (GpuRegion.Grow): the next upload clears the new buffer and copies the words the old one held into it on
    // the GPU, so every placed mesh stands where it was written and nothing it held is staged again, and the old buffer is
    // disposed once that upload completes (Retire). The region keeps its reserved copy sets, so growing takes no
    // descriptor range.
    // Shrinks the region to MeshRegionGrownBytes of the need when a relocation leaves it holding more than half as much
    // again, so the meshes no list names any more give back their device-local, staging and host-copy bytes. The shrunk
    // region owes its draw records; a relocation owes every mesh, which lands through the background work.
    private void FitMeshRegion(ulong bytes) {
        var fitted = MeshRegionGrownBytes(needBytes: bytes, mostBytes: Math.Max(val1: bytes, val2: MeshRegionLimitBytes));
        var held = m_meshRegion.ByteCount;

        // A shrink holds both buffers until the old one's readers finish: one the pool has no room for waits for a
        // later relocation, and the region keeps what it holds.
        if ((((ulong)held) <= (fitted + (fitted / 2UL))) || !m_meshGrant.TryReserveReplacement(newBytes: ((long)fitted))) {
            return;
        }

        IGpuBuffer replaced;

        try {
            replaced = m_meshRegion.Shrink(byteCount: ((long)fitted));
        } catch (GpuMemoryRefusedException) {
            m_meshGrant.Kept(refused: true);

            return;
        }

        ReplacedMeshRegion(newBytes: fitted, oldBytes: held, replaced: replaced);
    }
    // The region's buffer was replaced by one of another size: the next upload carries what the old one held, the old one
    // is retired behind that upload and leaves the grant's reservation when it is released, and every binding of the
    // region is written again.
    private void ReplacedMeshRegion(IDisposable replaced, long oldBytes, ulong newBytes) {
        RequireMeshUpload();
        Retire(replaced: new ReplacedRegion(bytes: oldBytes, grant: m_meshGrant, replaced: replaced));
        m_meshGrant.Grown(newBytes: ((long)newBytes), oldBytes: oldBytes);
        m_seedMeshHistory = true;
        m_bindingRevision++;
        Volatile.Write(
            location: ref m_meshRegionBytes,
            value: newBytes
        );
    }
    // The growth is reserved before the plan that asks for it (PrepareMeshRegionLimit), so the ledger admits the new
    // buffer against the grant. A growth refused all the same (the pool had no room for a replacement beside the
    // standing buffer, or the device said no) leaves the region as it is: the placements past what it holds are taken
    // back and refused (SdfMeshRegionArena.Clip), their draws wait, and nothing throws. Draw records the buffer cannot
    // hold are the floor, which a residency cannot draw without: their growth is reserved whatever the pool holds and
    // admitted past the limit, counted. Only a device that refuses that too is an error.
    private void EnsureMeshRegionCapacity(ulong bytes) {
        var current = ((ulong)m_meshRegion.ByteCount);

        if (bytes <= current) {
            m_meshGrant.Kept();

            return;
        }

        var grown = MeshRegionGrownBytes(needBytes: bytes, mostBytes: Math.Max(val1: bytes, val2: MeshRegionLimitBytes));
        IDisposable replaced;

        try {
            replaced = m_meshRegion.Grow(byteCount: ((long)grown));
        } catch (GpuMemoryRefusedException) {
            m_meshGrant.Kept(refused: true);
            m_meshArena.Clip(limitBytes: current);
            ForgetArenaMeshes();

            var floor = (((ulong)m_meshArena.Words) * sizeof(uint));

            if (floor <= current) {
                return;
            }

            grown = MeshRegionGrownBytes(needBytes: floor);
            m_meshGrant.ReserveFloorGrowth(newBytes: ((long)grown));
            try {
                replaced = m_meshRegion.Grow(byteCount: ((long)grown));
            } catch (GpuMemoryRefusedException refusal) {
                m_meshGrant.Kept(refused: true);
                throw new InvalidOperationException(innerException: refusal, message: $"The mesh region holds {current} bytes and the device refused the {grown} its draw records need.");
            }
        }

        ReplacedMeshRegion(newBytes: grown, oldBytes: ((long)current), replaced: replaced);
    }
}
