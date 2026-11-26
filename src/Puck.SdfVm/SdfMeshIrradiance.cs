using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Waiting;
using Puck.Abstractions;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One level's window of the mesh path's irradiance volume (<see cref="SdfMeshIrradiance"/>).</summary>
/// <param name="Spacing">The distance between neighbouring probes along each axis, in metres; zero for a level not
/// published.</param>
/// <param name="Dims">The probes along each axis of the window.</param>
/// <param name="Low">The window's first cell: a probe of cell <c>c</c> sits at <c>(c + 0.5) * Spacing</c>, per axis.</param>
public readonly record struct SdfMeshIrradianceWindow(Vector3 Spacing, (int X, int Y, int Z) Dims, (int X, int Y, int Z) Low) {
    /// <summary>Gets whether the window holds probes views read.</summary>
    public bool Present => ((Spacing.X > 0f) && (Spacing.Y > 0f) && (Spacing.Z > 0f) && (Dims.X > 0) && (Dims.Y > 0) && (Dims.Z > 0));
    /// <summary>Gets the probes the window holds.</summary>
    public int Count => ((Dims.X * Dims.Y) * Dims.Z);
}
/// <summary>The pass-block parameters of the mesh path's irradiance volume (<see cref="SdfMeshIrradiance"/>): the finest
/// level's window, and the coarser levels' about it.</summary>
/// <param name="Enabled">Whether views read the volume; false until a level has measured probes to read.</param>
/// <param name="Finest">The finest level's window.</param>
/// <param name="Middle">The second level's window.</param>
/// <param name="Far">The coarsest level's window.</param>
/// <param name="Geometry">The serial of the published geometry: it moves with each publication that resizes, respaces or
/// withdraws a window, or that copies a retrace of a probe its slot already held or a republication of every slot; never
/// with a relight, a window's move, or a probe traced into a cell no slot held. Each step takes a serial no other
/// publication of any volume in the process holds, never zero (zero is the parameters' default, a volume that published
/// nothing), so a gather a history keeps stands only under the serial it was taken at, in the volume it was taken in
/// (<c>sdf-probe-history.hlsli</c>).</param>
/// <param name="Windows">The serial of the published windows' places: it moves with each publication that moves a window.
/// A kept gather outlives such a move only where the windows still hold its receiver the way they did.</param>
public readonly record struct SdfMeshIrradianceParameters(bool Enabled, SdfMeshIrradianceWindow Finest, SdfMeshIrradianceWindow Middle = default, SdfMeshIrradianceWindow Far = default, uint Geometry = 0, uint Windows = 0) {
    /// <summary>Returns a level's window, finest first.</summary>
    /// <param name="level">The level, from zero to <see cref="SdfMeshIrradianceProbe.Levels"/> less one.</param>
    /// <returns>The window; a default window for a level out of range.</returns>
    public SdfMeshIrradianceWindow Window(int level) => level switch {
        0 => Finest,
        1 => Middle,
        2 => Far,
        _ => default,
    };
}
/// <summary>
/// The baked-mesh path's indirect lighting: a volume of irradiance probes about the first view's camera, traced on the
/// CPU against the frame's static meshes (<see cref="SdfMeshIrradianceScene"/>) and read by the views pass, which performs
/// no field query and traces nothing.
/// <para>A probe sits at the centre of a world cell, one <see cref="SdfMeshIrradianceWindow.Spacing"/> wide. Its
/// geometry is one hit buffer (<see cref="SdfMeshProbeDepth"/>): a closest hit per octahedral texel at full reach,
/// traced once per scene, from which come the share of the sky each of its six cube axes sees, the octahedral map of
/// first-hit surface support receivers are tested against, and whether the probe stands inside geometry. Its light is a
/// relight of those hits under the frame's lighting: the radiance the static geometry sends each axis (the directional
/// lights and the static point and spot lights under traced shadows, emission, and the light the geometry reflects from
/// the probes around each hit, its sky included) and which static point or spot lights (<see cref="SdfLight.IsTraced"/>)
/// its centre sees. A lighting change (a light, the sky, a gain, a material) costs a relight; only a change of the static
/// draws, the window or the tier costs a trace. The views pass multiplies the sky shares by the live sky irradiance, so
/// the sky term itself follows the sky at once. Each level's window is toroidal: a cell's probe lives in the slot its
/// coordinates name modulo the window's size, keyed by its cell, so the views pass rejects a slot holding another cell's
/// probe, and a move of the camera rewrites only the slots of cells it brings in.</para>
/// <para>The volume has <see cref="SdfMeshIrradianceProbe.Levels"/> nested levels about the camera, each window a
/// coarser lattice over a wider reach (<see cref="LevelsOf"/>), traced alike against the same scene. A receiver
/// reads the finest level whose window holds it, the coarsest past every window; where none of that level's probes
/// measured it, the same probes answer past their measured reach, blocked only by the surfaces they measured, so no
/// surface the camera sees reads the absence of a measurement as darkness.</para>
/// <para>Probes are traced and relit on the thread pool in batches, untraced probes first (finest level and nearest
/// the camera first), then the probes whose lighting is oldest. A relight reflects the light the volume holds about each
/// hit, so bounce accumulates over successive relights; a probe is relit until it has taken one relight a sweep under
/// the current lighting, and a lighting that changes every frame (a day's sun) keeps the relight cycling over the
/// window at the rate the budget grants each second of wall time (<see cref="SdfFrame.IndirectBudget"/>), whatever the frame rate. Overload lengthens the
/// lighting's age, never the frame, and is reported as an <c>[sdf.mesh-gi] behind</c> line. A lighting-converged frame relights its
/// window from no bounce through every sweep before it renders, so its light depends on its scene, lighting and window
/// alone. The dynamic draws (moving bodies) receive the volume's light and do not cast into it; an anchored light, which
/// moves, is neither traced nor occluded.</para>
/// </summary>
public sealed partial class SdfMeshIrradiance : IDisposable {
    private SdfMeshProbeGpuWork? m_gpuTrace;
    private SdfMeshProbeGpuWork? m_nextGpuTrace;
    private bool m_gpuReattach;

    /// <summary>Gets whether the latest staged frame asks the volume's rays to take the ray hardware where the device runs
    /// the probe kernels' ray-query variants (<see cref="SdfFrame.RayQueries"/>).</summary>
    public bool RayQueries { get => Volatile.Read(location: ref m_rayQueries); private set => Volatile.Write(location: ref m_rayQueries, value: value); }
    private bool m_rayQueries;
    internal SdfMeshProbeGpuWork? GpuTrace {
        get => m_gpuTrace;
        set { lock (m_gate) { m_nextGpuTrace = value; m_gpuReattach = true; } }
    }

    // A query of a scene the volume has since trimmed (TrimRetained) still asks for the volume's scene: both export the
    // same GPU words.
    internal bool KeepsGpuScene(SdfMeshIrradianceScene scene) {
        lock (m_gate) { return (!m_disposed && SdfMeshIrradianceScene.SameGpuContent(first: m_scene, second: scene)); }
    }
    internal bool KeepsGpuPublication(SdfMeshIrradianceScene scene, int geometry, int window) {
        lock (m_gate) { return (!m_disposed && SdfMeshIrradianceScene.SameGpuContent(first: m_scene, second: scene) && (m_geometry == geometry) && (m_windowVersion == window)); }
    }
    // Whether the installed scene names a mesh its residency no longer places, so no query of it can be admitted
    // (SdfWorldTables.TryProbeScene): a group that traces the tier it draws leaves the scene when the view retiers it and
    // the region forgets the tier it drew. The probe service is the one writer (NoteScenePlacement), since only an
    // admission reads the placements; Install clears it with the scene. ScenePendingDue is the one reader: the trace's
    // progress paces a rebuild only while the installed scene can be traced, so a scene that cannot never holds back
    // the build that replaces it.
    private volatile bool m_sceneUnplaced;
    /// <summary>Records whether a query's scene could be bound when the probe service last tried to admit it, for the
    /// scene the volume holds installed: a scene that stayed unbound past the service's admission wait makes the next
    /// scene build due whatever the trace still owes (<see cref="ScenePendingDue"/>).</summary>
    /// <param name="scene">The scene the query asked for.</param>
    /// <param name="placed">Whether the residency places every mesh the scene names.</param>
    internal void NoteScenePlacement(SdfMeshIrradianceScene scene, bool placed) {
        if (m_sceneUnplaced == !placed) { return; }
        lock (m_gate) {
            if (m_disposed || !SdfMeshIrradianceScene.SameGpuContent(first: m_scene, second: scene)) { return; }
            m_sceneUnplaced = !placed;
        }
        if (!placed) { Counters.Add(SceneUnplaced, 1); }
    }

    private bool m_disposed;
    private bool m_trimListening;
    private TimeProvider m_time = TimeProvider.System;

    /// <summary>Gets or sets the clock that meters the volume's work: the budget's grants, the intervals between its
    /// scene builds and lighting captures, and the ages of its probes' light. It is the device's
    /// (<see cref="Puck.Abstractions.Gpu.IGpuDeviceContext.Time"/>), which the residency hands over before its first
    /// frame stages; a wait on the volume's trace is bounded in real time whatever it reads. Set before the first frame
    /// stages.</summary>
    public TimeProvider Time {
        get => Volatile.Read(location: ref m_time);
        set => Volatile.Write(location: ref m_time, value: (value ?? throw new ArgumentNullException(paramName: nameof(value))));
    }

    // Name the probe caches' tables (managed) and the blocks their entries name (native when the volume has an
    // allocator) in their rows of the process account, read when the account is measured. Disposed outside m_gate, which
    // their reads take while the account holds its sample gate.
    private readonly IDisposable m_cacheOwner;
    private readonly IDisposable m_blockOwner;
    private readonly Puck.Hosting.Jobs.MemoryAccount.Owner m_volumeOwner = Puck.Hosting.Jobs.MemoryAccount.Shared.CreateOwner(
        name: "sdf.mesh-gi.volume", memoryClass: MemoryClass.Elastic);
    // The allocator the volume's slot words, the batches' hit and light blocks and the hit words a batch passes through
    // take their storage from, so none of them lives on the large object heap; null (offline callers) takes the
    // process's (ProcessAllocator).
    private readonly IAllocator? m_allocator;

    /// <summary>Registers the probe cache's sampled storage in the process account.</summary>
    /// <param name="allocator">The native allocator the volume's large storage comes from (the residency's mesh library's),
    /// or null for the process's.</param>
    public SdfMeshIrradiance(IAllocator? allocator = null) {
        m_allocator = allocator;
        m_levels = CreateLevels(blocks: m_blocks);
        m_cacheOwner = Puck.Hosting.Jobs.MemoryAccount.Shared.CreateSampledOwner(backing: MemoryBacking.Managed, memoryClass: MemoryClass.Cached, name: "sdf.mesh-gi.probe-cache", read: CacheTableBytes);
        m_blockOwner = Puck.Hosting.Jobs.MemoryAccount.Shared.CreateSampledOwner(backing: MemoryBacking.NativeAllocator, memoryClass: MemoryClass.Cached, name: "sdf.mesh-gi.probe-blocks", read: () => Interlocked.Read(location: ref m_blockBytes));
    }

    // Drops the geometry the scene retained for a later build. The GPU client still holds the untrimmed scene it uploaded,
    // whose claim alone keeps that geometry alive, so it adopts the trimmed one, which exports the same words, without an
    // upload. The client's gate is taken after this one is left: its admission reads this gate under its own.
    private void TrimRetained() {
        SdfMeshIrradianceScene? trimmed;
        SdfMeshProbeGpuWork? client;

        lock (m_gate) {
            if (m_disposed || (m_scene is null)) { return; }
            var previous = m_scene;

            m_scene = previous.WithoutRetained();
            previous.Dispose();
            trimmed = m_scene.Acquire();
            client = m_gpuTrace;
        }
        try {
            client?.Adopt(scene: trimmed);
        } finally {
            trimmed.Dispose();
        }
    }

    /// <summary>Stops new work and returns at once; the last scene is released once the scene and trace jobs still
    /// borrowing it finish, on the thread pool, so a caller on the frame thread never waits on background work.</summary>
    public void Dispose() {
        Task[] jobs;

        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            if (m_trimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested -= TrimRetained; m_trimListening = false; }
            m_owed = m_traceOwed = m_refillOwed = false;
            // A scene waiting for a trace unit to end is dropped, so its build ends with the jobs below.
            if (m_pendingInstall is { } install) {
                m_pendingInstall = null;
                install.Scene.Dispose();
                install.Done.TrySetResult();
            }
            jobs = new[] { m_build, m_refill, m_worker, m_drawnBuild }.OfType<Task>().ToArray();
        }
        m_cacheOwner.Dispose();
        if (jobs.Length == 0) { ReleaseScene(); return; }
        _ = Task.WhenAll(jobs).ContinueWith(continuationAction: _ => ReleaseScene(), scheduler: TaskScheduler.Default);
    }

    private void ReleaseScene() {
        lock (m_gate) {
            m_scene?.Dispose();
            m_scene = null;
            m_drawnScene?.Dispose();
            m_drawnScene = null;
            m_drawnDraws = [];
            m_drawnClaims.Dispose();
            m_materialsScene = null;
            RentedArrays.Return(array: m_diagnosticDraws);
            RentedArrays.Return(array: m_snapshotDraws);
            (m_diagnosticDraws, m_diagnosticCount, m_snapshotDraws) = ([], 0, []);
            m_snapshotClaims.Dispose();
            // Native storage has no collector to fall back on: every block an entry names and the volume's words go to
            // the reader epochs, which free them once no reader's hold can reach them.
            foreach (var level in m_levels) { level.Cache.Clear(); }
            RetireVolume(next: VolumeWords.None);
            m_volumeOwner.Dispose();
            m_gpuAnswer?.Dispose();
            m_gpuAnswer = null;
        }
        ReclaimReleased(attempt: 0);
    }
    // Frees what the reader epochs still hold after the release. A hold a reader opened before it may still be open; what
    // that hold could read is freed by a later attempt, on a timer rather than a wait, once it has closed.
    private void ReclaimReleased(int attempt) {
        _ = m_blocks.Reclaim(release: static item => item.Reclaim());
        if (m_blocks.Pending == 0) {
            m_blockOwner.Dispose();
            return;
        }
        if (attempt < ReleaseAttempts) {
            _ = Task.Delay(millisecondsDelay: 50).ContinueWith(continuationAction: _ => ReclaimReleased(attempt: (attempt + 1)), scheduler: TaskScheduler.Default);
        }
    }
    private const int ReleaseAttempts = 200;

    // Each tier's levels, finest first. The finest is the tier's own; the storey level's 2 m vertical spacing puts a probe
    // plane in every storey 2.4 m or taller, which no isotropic coarse level does, over a 128 m by 48 m by 128 m window;
    // the 32 m level reaches past it.
    private static readonly SdfMeshIrradianceLevel StoreyLevel = new(Dims: (32, 24, 32), Sheets: true, Spacing: new Vector3(x: 4f, y: 2f, z: 4f));
    private static readonly SdfMeshIrradianceLevel FarLevel = new(Dims: (32, 12, 32), Sheets: true, Spacing: new Vector3(value: 32f));
    private static readonly SdfMeshIrradianceLevel[] HighLevels = [new(Dims: (48, 16, 48), Sheets: false, Spacing: new Vector3(value: SdfMeshIrradianceProbe.FinestSpacing)), StoreyLevel, FarLevel];
    private static readonly SdfMeshIrradianceLevel[] MediumLevels = [new(Dims: (32, 12, 32), Sheets: false, Spacing: new Vector3(value: 2f)), StoreyLevel, FarLevel];

    /// <summary>Returns a tier's levels, finest first: each one's cell size and its window's probes along each axis.</summary>
    /// <param name="tier">The indirect tier.</param>
    /// <returns><see cref="SdfMeshIrradianceProbe.Levels"/> levels.</returns>
    public static ReadOnlySpan<SdfMeshIrradianceLevel> LevelsOf(SdfIndirectTier tier) => ((tier == SdfIndirectTier.High) ? HighLevels : MediumLevels);

    /// <summary>The most probes a volume holds, over every tier's levels.</summary>
    public static readonly int MaxProbes = Math.Max(val1: HighLevels.Sum(selector: static level => level.Count), val2: MediumLevels.Sum(selector: static level => level.Count));

    /// <summary>The most, in metres, a traced surface stands from a static mesh's own triangles: the trace scene takes each
    /// mesh's coarsest level within it (<see cref="SdfMeshIrradianceScene.Build"/>). It is the receiver's normal bias
    /// (<see cref="SdfMeshIrradianceProbe.NormalBias"/>), a twentieth of the finest probe spacing: a surface moved less
    /// than the offset a receiver already stands off its own surface changes no side a probe sees it from, while the
    /// coarser levels hold a fraction of the triangles every bottom, hierarchy and probe-scene word is sized by.</summary>
    public const float TraceError = SdfMeshIrradianceProbe.NormalBias;
    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-gi";
    /// <summary>The probe slots a gather fills: the chosen stage's eight corners, then the remainder stage's eight.</summary>
    public const int GatherSlots = 16;

    // Declaring a kind is what lists it in the counters: the kinds below initialize in textual order after this field.
    private static readonly WorkKindRegistry Registry = new();

    /// <summary>Counts each trace scene built for a change of the static draws.</summary>
    public static readonly WorkKind Scenes = Registry.Declare(name: "sdf.mesh-gi.scenes", unit: "scenes", workClass: WorkClass.Pacing);
    /// <summary>Counts each geometry a scene build built: one no earlier scene held.</summary>
    public static readonly WorkKind GeometriesBuilt = Registry.Declare(name: "sdf.mesh-gi.geometries-built", unit: "geometries", workClass: WorkClass.Pacing);
    /// <summary>Counts each geometry a scene build took from the scene it replaced.</summary>
    public static readonly WorkKind GeometriesReused = Registry.Declare(name: "sdf.mesh-gi.geometries-reused", unit: "geometries", workClass: WorkClass.Pacing);
    /// <summary>Counts the triangles of the geometries scene builds built.</summary>
    public static readonly WorkKind TrianglesBuilt = Registry.Declare(name: "sdf.mesh-gi.triangles-built", unit: "triangles", workClass: WorkClass.Pacing);
    /// <summary>Counts each probe whose hits were traced: its placement and its hit buffer.</summary>
    public static readonly WorkKind Traced = Registry.Declare(name: "sdf.mesh-gi.traced", unit: "probes", workClass: WorkClass.Pacing);
    /// <summary>Counts each probe relight: its radiance cubes and light visibility shaded from its cached hits.</summary>
    public static readonly WorkKind Relit = Registry.Declare(name: "sdf.mesh-gi.relit", unit: "probes", workClass: WorkClass.Pacing);
    /// <summary>Counts each relight of a probe relit before under another geometry that kept the cube its point and
    /// spot lights send, since no change since reached the shadow segments of its hits.</summary>
    public static readonly WorkKind LocalKept = Registry.Declare(name: "sdf.mesh-gi.local-kept", unit: "probes", workClass: WorkClass.Pacing);
    /// <summary>Counts rays in completed GPU trace jobs, including placement trials and subsequently invalidated batches.</summary>
    public static readonly WorkKind GpuRays = Registry.Declare(name: "sdf.mesh-gi.gpu-rays", unit: "rays", workClass: WorkClass.Pacing);
    /// <summary>Measured GPU time of completed trace submissions, including their hierarchy upload and readback copy.</summary>
    public static readonly WorkKind GpuTraceNanoseconds = Registry.Declare(name: "sdf.mesh-gi.gpu-trace-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Measured GPU time of completed relight submissions and feedback updates.</summary>
    public static readonly WorkKind GpuRelightNanoseconds = Registry.Declare(name: "sdf.mesh-gi.gpu-relight-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Counts the rays of <see cref="GpuRays"/> a ray query traced through the scene's acceleration structures;
    /// the rest took the software walk.</summary>
    public static readonly WorkKind GpuQueryRays = Registry.Declare(name: "sdf.mesh-gi.gpu-query-rays", unit: "rays", workClass: WorkClass.Pacing);
    /// <summary>The share of <see cref="GpuTraceNanoseconds"/> spent by trace submissions whose rays a ray query traced:
    /// rays a second for each walk are <see cref="GpuQueryRays"/> over this, and the remainders over the remainder.</summary>
    public static readonly WorkKind GpuQueryTraceNanoseconds = Registry.Declare(name: "sdf.mesh-gi.gpu-query-trace-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>The share of <see cref="GpuRelightNanoseconds"/> spent by relights whose shadow rays a ray query traced.</summary>
    public static readonly WorkKind GpuQueryRelightNanoseconds = Registry.Declare(name: "sdf.mesh-gi.gpu-query-relight-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Measured GPU time of the scene's acceleration-structure builds and compactions.</summary>
    public static readonly WorkKind GpuStructureNanoseconds = Registry.Declare(name: "sdf.mesh-gi.gpu-structure-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Counts the bottom-level acceleration structures built, one a geometry each published scene lacked.</summary>
    public static readonly WorkKind StructuresBuilt = Registry.Declare(name: "sdf.mesh-gi.structures-built", unit: "structures", workClass: WorkClass.Pacing);
    /// <summary>Counts the answered probe jobs of each kind, indexed trace, relight, select, camera sample.</summary>
    public static readonly WorkKind[] GpuJobs = DeclareJobKinds(measure: "jobs", unit: "jobs");
    /// <summary>Counts the units the answered probe jobs of each kind held (a relight's predicted rays,
    /// a selection's tests, any other kind's origins), indexed as <see cref="GpuJobs"/>.</summary>
    public static readonly WorkKind[] GpuJobUnits = DeclareJobKinds(measure: "job-units", unit: "units");
    /// <summary>Counts the wall time, in nanoseconds, from a worker's submission of each answered probe job to its
    /// answer, indexed as <see cref="GpuJobs"/>.</summary>
    public static readonly WorkKind[] GpuJobWaitNanoseconds = DeclareJobKinds(measure: "job-wait-nanoseconds", unit: "nanoseconds");
    private static WorkKind[] DeclareJobKinds(string measure, string unit) =>
        [.. ((string[])["trace", "relight", "select", "camera-sample"]).Select(kind => Registry.Declare(name: $"sdf.mesh-gi.gpu-{measure}.{kind}", unit: unit, workClass: WorkClass.Pacing))];
    /// <summary>Measured GPU time spent selecting probes owing work.</summary>
    public static readonly WorkKind GpuSelectNanoseconds = Registry.Declare(name: "sdf.mesh-gi.gpu-select-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Queued trace jobs cancelled because their scene was replaced before admission.</summary>
    public static readonly WorkKind GpuCancelled = Registry.Declare(name: "sdf.mesh-gi.gpu-cancelled", unit: "jobs", workClass: WorkClass.Pacing);
    /// <summary>The times the probe service found the installed scene naming a mesh its residency no longer places
    /// (<see cref="NoteScenePlacement"/>), each of which makes the next scene build due.</summary>
    public static readonly WorkKind SceneUnplaced = Registry.Declare(name: "sdf.mesh-gi.scene-unplaced", unit: "scenes", workClass: WorkClass.Pacing);
    /// <summary>Counts background admission checks that walk a scene's mesh placements.</summary>
    public static readonly WorkKind PlacementChecks = Registry.Declare(name: "sdf.mesh-gi.placement-checks", unit: "checks", workClass: WorkClass.Pacing);
    /// <summary>Counts admissions that reuse a successful check of the scene's current placements and backing.</summary>
    public static readonly WorkKind PlacementChecksReused = Registry.Declare(name: "sdf.mesh-gi.placement-checks-reused", unit: "checks", workClass: WorkClass.Pacing);
    /// <summary>Traversal stack or step-limit assertions; valid scenes must leave this at zero.</summary>
    public static readonly WorkKind TraversalFaults = Registry.Declare(name: "sdf.mesh-gi.traversal-faults", unit: "walks", workClass: WorkClass.Pacing);
    /// <summary>CPU sky captures for probe lighting: each renders the sky map and averages it over the hit texels.</summary>
    public static readonly WorkKind SkyCaptures = Registry.Declare(name: "sdf.mesh-gi.sky-captures", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts each relight cycle whose oldest lighting was past the budget's age target.</summary>
    public static readonly WorkKind Behind = Registry.Declare(name: "sdf.mesh-gi.behind", unit: "cycles", workClass: WorkClass.Pacing);
    /// <summary>Counts camera hit traces, including rejected samples inside opaque geometry.</summary>
    public static readonly WorkKind CameraTraces = Registry.Declare(name: "sdf.mesh-gi.camera-traces", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Counts relights of a cached camera sample's hits under a changed lighting.</summary>
    public static readonly WorkKind CameraRelights = Registry.Declare(name: "sdf.mesh-gi.camera-relights", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Counts each camera sample asked of the volume (<see cref="TrySampleCamera"/>).</summary>
    public static readonly WorkKind CameraSamples = Registry.Declare(name: "sdf.mesh-gi.camera-samples", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Device time spent gathering or tracing exposure meter samples.</summary>
    public static readonly WorkKind GpuCameraSampleNanoseconds = Registry.Declare(name: "sdf.mesh-gi.gpu-camera-sample-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Counts the static draws scene builds took into their trace scenes.</summary>
    public static readonly WorkKind StaticDraws = Registry.Declare(name: "sdf.mesh-gi.static-draws", unit: "draws", workClass: WorkClass.Pacing);
    /// <summary>Counts the top-level entries scene builds made: one a braid of a static draw.</summary>
    public static readonly WorkKind Entries = Registry.Declare(name: "sdf.mesh-gi.entries", unit: "entries", workClass: WorkClass.Pacing);
    /// <summary>Counts the bytes of the prefix each scene export packs on the host (the top level's nodes, its entries
    /// and one record an instance), which its upload sends whole.</summary>
    public static readonly WorkKind PrefixBytes = Registry.Declare(name: "sdf.mesh-gi.prefix-bytes", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the most top-level entries one scene build made since the peaks were last cleared.</summary>
    public static readonly WorkKind PeakEntries = Registry.DeclarePeak(name: "sdf.mesh-gi.peak.entries", unit: "entries", workClass: WorkClass.Pacing);
    /// <summary>Gets the most prefix bytes one scene export packed since the peaks were last cleared.</summary>
    public static readonly WorkKind PeakPrefixBytes = Registry.DeclarePeak(name: "sdf.mesh-gi.peak.prefix-bytes", unit: "bytes", workClass: WorkClass.Pacing);

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// volume exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => Registry.Kinds;

    /// <summary>Gets the volume's counts: its scene builds, the geometry and entries they built and reused, the prefix
    /// bytes their exports pack, its probe traces and relights, and its behind cycles.</summary>
    public WorkCounterSet Counters { get; } = new(kinds: Kinds, name: SourceName);

    private static readonly Vector3[] PlacementDirections = BuildDirections(count: 32);

    private static Vector3[] BuildDirections(int count) {
        var directions = new Vector3[count];

        for (var ray = 0; (ray < count); ray++) { directions[ray] = LowDiscrepancy.SphericalFibonacci(count: count, index: ray); }
        return directions;
    }

    // Each cube axis's cosine-weighted share of the sphere under the hit buffer's quadrature: the normalisation of a cube
    // axis's sky share and radiance.
    // The cube axes, in the order a cube stores them: +X, -X, +Y, -Y, +Z, -Z.
    private static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
    private static readonly float[] AxisTotals = BuildAxisTotals();

    private static float[] BuildAxisTotals() {
        var totals = new float[6];
        var directions = SdfMeshProbeDepth.RayDirections;
        var weights = SdfMeshProbeDepth.RayWeights;

        for (var texel = 0; (texel < directions.Length); texel++) {
            for (var axis = 0; (axis < 6); axis++) { totals[axis] += (weights[texel] * MathF.Max(x: 0f, y: Vector3.Dot(vector1: directions[texel], vector2: Axes[axis]))); }
        }
        return totals;
    }

    // The probes one trace batch takes before it looks again at the camera, the lighting and the budget: a scene change
    // drops the batch in flight, so a trace batch stays small while bakes land.
    private const int BatchProbes = 384;
    // The probes a relight batch takes at most, within the frame's relight credit, and a converged frame's batch. A batch
    // is one round trip through the probe service, which cuts it into jobs within the live allowance, so a batch's size
    // bounds probes per round trip, not GPU time per frame: a batch smaller than the frame's credit leaves the GPU idle
    // between round trips and the authored relightPerFrame unspent. Larger bounded batches amortize completion/pump
    // latency without submitting the entire High window as one long-running dispatch. CPU reference uses the same
    // boundaries.
    private const int DrainBatchProbes = 2048;

    // The trace's workers: a lighting-converged frame waits for its window traced to the end, so it traces on all but two of the
    // machine's processors; a trace behind rendered frames takes a quarter of them, so the frame thread keeps a core of
    // its own and the memory bandwidth it packs and uploads with.
    private static readonly int DrainWorkers = Math.Max(val1: 1, val2: (Environment.ProcessorCount - 2));
    private static readonly int BackgroundWorkers = Math.Max(val1: 1, val2: (Environment.ProcessorCount / 4));
    // The least wall time between two scene builds of frames that do not converge lighting (ScenePendingDue), so the
    // trace measures what has landed without restarting on every arrival. Presentation only: the scene a frame lights by
    // is never simulation state.
    private static readonly TimeSpan PendingSceneInterval = TimeSpan.FromSeconds(value: 4d);
    // Continuous asynchronous captures are throttled because they render and project the sky on the frame thread.
    private static readonly TimeSpan LightingInterval = TimeSpan.FromSeconds(value: 0.1d);

    // The most relights a probe takes under one lighting while its bounce converges, and the change a relight may make
    // to an axis's luminance, as a share of it plus a floor in nits, for the bounce to count as converged.
    private const int MaxSweeps = 32;
    // The geometry changes the volume remembers; a probe traced before the oldest traces again.
    private const int MaxChanges = 16;
    // The lighting versions whose capture times the volume remembers.
    private const int MaxVersionTimes = 4096;
    private const float ConvergedShare = 0.02f;
    private const float ConvergedFloor = 1.0e-3f;
    // Continuous asynchronous skies keep the preceding capture while every sky texel's channel is within this share of
    // its own value in either capture, so no probe's sky light moves past it, however few rays reach the sky.
    private const float SkyTolerance = 1.0e-3f;

    // The least wall time between two behind lines.
    private static readonly TimeSpan BehindInterval = TimeSpan.FromSeconds(value: 10d);
    private readonly Lock m_gate = new();
    private readonly Level[] m_levels;
    // The holds every read of the probe caches takes, and the blocks their entries dropped, which return to the pool
    // once no hold that could read them is open (ProbeStorage).
    private readonly ReaderEpochs<Reclaimable> m_blocks = new();

    private static Level[] CreateLevels(ReaderEpochs<Reclaimable> blocks) {
        var levels = new Level[SdfMeshIrradianceProbe.Levels];

        for (var index = 0; (index < levels.Length); index++) { levels[index] = new Level(index: index, blocks: blocks); }
        return levels;
    }

    /// <summary>Describes the probe caches the volume keeps across window moves, over every level: their probes, their
    /// tables' capacity and bytes, and the bytes of the hit and light blocks their entries hold. Safe to read from any
    /// thread; accounting holds the volume gate while walking retained slots.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeCache() {
        var (table, blocks, capacity, count) = CacheFigures();

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"probe-cache-probes={count} probe-cache-capacity={capacity} probe-cache-table={table}B probe-cache-blocks={blocks}B probe-cache-bytes={(table + blocks)}B trace-scene-cpu-array-bytes={(m_scene?.HierarchyBytes ?? 0)} {GpuTrace?.DescribeMemory()}");
    }

    // The probe caches' table bytes over every level; the blocks their entries name are read apart (m_blockOwner), since
    // they lie in another row.
    private long CacheTableBytes() => CacheFigures().Table;
    // Every level's probe cache: its table bytes, capacity and probes, summed, and the bytes of the hit and light blocks
    // the volume holds (ProbeStorage.Charge): every block not yet returned, named by an entry, a batch in flight or a
    // retired table a reader may still hold.
    private (long Table, long Blocks, int Capacity, int Count) CacheFigures() {
        long table = 0;
        int capacity = 0, count = 0;

        lock (m_gate) {
            foreach (var level in m_levels) {
                var (levelTable, levelCapacity) = level.Cache.Bytes();

                table += levelTable;
                capacity += levelCapacity;
                count += level.Cache.Count;
            }
        }

        return (table, Interlocked.Read(location: ref m_blockBytes), capacity, count);
    }
    // The bytes of the hit and light blocks the volume holds, kept as they are charged and returned (ProbeStorage).
    private long m_blockBytes;

    // The worker's candidates and a batch's results, kept across batches: one worker runs at a time.
    private readonly List<Candidate> m_candidates = [];

    private int m_selectionLighting;
    private int m_selectionSweep;
    private bool m_interleaving;

    private CameraSnapshot[] m_viewCameras = [];
    private ProbeData[] m_results = [];
    private readonly List<float> m_ages = [];
    private readonly float[] m_listAgeMax = new float[SdfMeshIrradianceProbe.Levels];
    private readonly int[] m_listAgeCount = new int[SdfMeshIrradianceProbe.Levels];

    // The geometry, windows, layout, sweep and lighting the worker's list was made for; where its traces end and the
    // overdue or lighting-stale relights after them end; where the worker stands in its traces and in its relights; and
    // the time it spent on each since listing (Work). The worker's alone, as the list is.
    private bool m_listed;
    private int m_listGeometry;
    private int m_listWindow;
    private Layout m_listLayout;
    private int m_listSweep;
    private int m_listLighting;
    private int m_listTraces;
    private int m_listUrgent;
    private int m_listTraceCursor;
    private int m_listRelightCursor;
    private long m_listTraceTicks;
    private long m_listRelightTicks;

    // The worker's store of a batch's landed probes (StoreLanded): the cells that landed, the window cells they restore,
    // and the slot words composed for those.
    private readonly List<(int Level, (int X, int Y, int Z) Cell)> m_landed = [];
    private readonly List<(int Level, (int X, int Y, int Z) Cell)> m_restoreCells = [];
    // The volume's slot words. A reader outside the gate takes the reference under the gate inside an open hold of
    // m_blocks, which a replacement retires the old words to (RetireVolume), so they are freed only once it ends.
    private VolumeWords m_volume = VolumeWords.None;
    private readonly List<int> m_changedSlots = [];
    private readonly Stopwatch m_clock = new();
    // When the latest scene build began, a timestamp of Time; zero before the first.
    private long m_sceneStartedAt;
    private long? m_sceneRefusedAt;
    private long? m_diagnosticRefusedAt;

    private SdfWorldTables? m_writeTables;
    private Action<ReadOnlyMemory<uint>>? m_write;
    private SdfMeshIrradianceScene? m_scene;

    /// <summary>Acquires an opaque static BVH of the supplied frame's exact drawn static meshes: the trace scene where it
    /// holds them, and otherwise, where the trace scene holds groups' floors in their place (<see cref="SdfMeshDraw.Trace"/>),
    /// a scene of the drawn meshes this call builds on the thread pool and keeps while they stand.</summary>
    /// <param name="frame">The retained rendered frame.</param>
    /// <returns>The matching scene under a claim the caller disposes, or null while absent or building.</returns>
    public SdfMeshIrradianceScene? DiagnosticScene(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        lock (m_gate) {
            if (m_disposed) { return null; }
            if (Draws(draws: m_diagnosticDraws, count: m_diagnosticCount, frame: frame)) { return m_scene?.Acquire(); }
            if ((m_drawnScene is { } drawn) && Draws(draws: m_drawnDraws, count: m_drawnDraws.Length, frame: frame)) { return drawn.Acquire(); }
            if (m_drawnBuild is { IsCompleted: false } || WaitingForMemory(m_diagnosticRefusedAt)) { return null; }

            var rows = frame.DrawRows;
            var input = new List<SdfMeshDraw>();

            for (var row = 0; (row < rows.Count); row++) {
                if (rows[row] is { IsStatic: true } draw) { input.Add(item: draw); }
            }
            m_drawnClaims.Update(draws: input);

            var previous = (m_drawnScene ?? m_scene)?.Acquire();

            m_drawnBuild = Task.Run(action: () => {
                SdfMeshIrradianceScene? scene;

                using (previous) { scene = TryBuildScene(input, previous); }
                lock (m_gate) {
                    if (scene is null) {
                        m_diagnosticRefusedAt = Time.GetTimestamp();
                        m_drawnClaims.Update(draws: Array.Empty<SdfMeshDraw>());
                        return;
                    }
                    m_diagnosticRefusedAt = null;
                    if (m_disposed) { scene.Dispose(); return; }
                    m_drawnScene?.Dispose();
                    (m_drawnScene, m_drawnDraws) = (scene, [.. input]);
                }
            });
            return null;
        }
    }
    // Whether a frame's static draws, as drawn, are the given draws, counted as multisets of their mesh, transform and
    // material. Under the gate.
    private static bool Draws(SdfMeshDraw[] draws, int count, SdfFrame frame) {
        var counts = new Dictionary<(ByReference<SdfMesh> Mesh, Matrix4x4 ObjectToWorld, int Material), int>();

        for (var i = 0; (i < count); i++) {
            var draw = draws[i];
            if (!HasComparableTransform(in draw)) { return false; }
            (ByReference<SdfMesh>, Matrix4x4, int) key = (draw.Mesh, draw.FixedPlacement, draw.Material);

            counts[key] = (counts.GetValueOrDefault(key: key) + 1);
        }
        var rows = frame.DrawRows;

        for (var row = 0; (row < rows.Count); row++) {
            var draw = rows[row];

            if (!draw.InStaticScene) { continue; }
            if (!HasComparableTransform(in draw)) { return false; }
            (ByReference<SdfMesh>, Matrix4x4, int) key = (draw.Mesh, draw.FixedPlacement, draw.Material);

            if (!counts.TryGetValue(key: key, value: out var left)) { return false; }
            if (left == 1) { counts.Remove(key: key); } else { counts[key] = (left - 1); }
        }
        return (counts.Count == 0);
    }
    // The diagnostics' scene of the drawn static meshes (DiagnosticScene), its draws, the meshes its build reads, claimed
    // as it starts, and the build. Under the gate.
    private SdfMeshIrradianceScene? m_drawnScene;
    private SdfMeshDraw[] m_drawnDraws = [];
    private readonly SdfMeshClaims m_drawnClaims = new();
    private Task? m_drawnBuild;

    // The static draws the installed scene was built from (the first m_diagnosticCount of the array), and the array the
    // next build's snapshot is written into, both rented. Builds run one at a time and swap the two as one installs, so
    // the spare is read by nobody: the build that read it as its previous draws finished before the next one starts.
    private SdfMeshDraw[] m_diagnosticDraws = [];

    private int m_diagnosticCount;

    private SdfMeshDraw[] m_snapshotDraws = [];
    // The meshes the latest snapshot names, claimed from the frame thread as the snapshot is taken and held until the
    // next snapshot moves them, so a build reads only claimed meshes; a snapshot naming the meshes the last one named
    // takes no claim.
    private readonly SdfMeshClaims m_snapshotClaims = new();
    // A build's count of the static draws the scene it replaces held, kept across builds (one runs at a time).
    private readonly Dictionary<(ByReference<SdfMesh> Mesh, Matrix4x4 ObjectToWorld, int Material), int> m_changeCounts = new();
    private readonly List<Box> m_changeBoxes = [];

    // The static-draw digests of the installed scene and of the build in flight; null for none.
    private MultisetDigest? m_sceneKey;
    private MultisetDigest? m_buildingKey;

    // The static-draw key as StaticKey last folded it, and what it read: the list and its journal, the journal serial and
    // list revision, and per draw whether it was static and its own digest. A frame whose journal names only rows that
    // are and were not static, or that digest as they did, keeps the key without reading the rest of the list.
    private readonly Puck.Abstractions.Gpu.GpuRowCursor m_keyCursor = new();

    private MultisetDigest m_keyValue;
    private int m_keyStatic;
    private int m_keyExtent;
    private long m_skipKeyThrough;

    private ulong[] m_drawKeys = [];
    private bool[] m_drawStatic = [];
    private readonly HashSet<int> m_staticRows = [];
    private readonly Puck.Abstractions.Gpu.GpuUploadRuns m_keyRuns = new(capacity: Puck.Abstractions.Gpu.GpuRegion.MaxCopyRuns);

    private Task? m_build;
    private Lighting? m_lighting;
    private ulong m_lightingKey;
    private SdfSkyBlock m_lightingSky;

    private readonly SdfSkyLayer[] m_lightingSkyLayers = new SdfSkyLayer[SdfSky.MaxLayers];

    private ulong m_localKey;
    private int m_localVersion;
    private long m_lightingCheckedAt;
    private int m_lightingVersion;
    private SdfAppearance? m_materialsAppearance;
    private SdfMeshIrradianceScene? m_materialsScene;
    private ulong m_materialsKey;

    private readonly SdfSkyLayer[] m_skyLayers = new SdfSkyLayer[SdfSky.MaxLayers];
    private readonly SdfSkyDetails m_skyDetails = new();

    // The volume's geometry: advanced by a scene build, a layout change or a change of the rays' reach, so every probe
    // traced before it that the change may reach traces again; the latest changes, each with the boxes of the static
    // draws it added or removed, null for every probe.
    private int m_geometry;

    private readonly List<(int Geometry, ChangeTree? Tree)> m_changes = [];
    // When each recent lighting version was captured, from the version m_versionBase on: a probe's lighting has been
    // stale since the version after its own was captured.
    private readonly List<long> m_versionTimes = [];
    private int m_versionBase = 1;

    private Layout m_layout;
    private SdfMeshIrradianceParameters m_published;
    private SdfIndirectBudget m_budget;
    // The budget's grants not yet spent: probes the worker may relight and trace before the next frame grants more.
    private double m_relightCredits;
    private double m_traceCredits;
    // The window's lighting age at the start of the latest relight cycle, in seconds: its median and oldest probe.
    private float m_ageMedian;
    private float m_ageMax;
    private long m_behindAt;
    private long m_cycleStart;
    private long m_cycleRelit;
    private float m_cycleRate;
    // The windows' refill (Refill): the windows' version, which a move of any level advances, whether its refill is
    // owed, the refill in flight on the thread pool and whether it still runs, and the cells it lists and the slot words
    // it composes for them.
    private int m_windowVersion;
    private bool m_refillOwed;
    private bool m_refillRunning;
    private Task? m_refill;

    private readonly List<(int Level, (int X, int Y, int Z) Cell)> m_refillCells = [];
    private CameraSample?[] m_cameraSamples = [];

    private int m_nextCameraSample;
    private Vector3 m_camera;
    private bool m_windowValid;
    private Task? m_worker;
    // Whether the windows may hold probes the worker has not yet traced or relit under the current lighting: set when the
    // scene, the lighting or a window changes, cleared when the worker finds nothing left to do. m_traceOwed is the part
    // a geometry change owes: probes not yet traced against the current scene.
    private bool m_owed;
    private bool m_traceOwed;

    // The bounce iteration: the sweep the worker is relighting the windows in (every probe once a sweep), whether a
    // probe of this sweep moved past the convergence bound, the sweeps since the inputs last changed, and whether a
    // lockstep frame waits for the worker to yield. Under the gate.
    private int m_sweep = 1;

    private bool m_sweepMoved;
    private int m_sweepsSince;
    private bool m_yield;
    private bool m_unconvergedReported;
    // The state the cache's light was drained for, cleared once asynchronous work relights any of it: a lighting-converged frame
    // whose state differs drains, so its light never carries the bounce asynchronous frames left.
    private (int Geometry, int Lighting, int Windows, Layout Layout) m_drained;

    // Whether a level holds slots composed since its last publication.
    private bool Dirty {
        get {
            foreach (var level in m_levels) {
                if (level.Changed.Count > 0) { return true; }
            }
            return false;
        }
    }

    // Whether the latest Stage wrote the volume table: the frame it prepared must render even once the work is done.
    // m_geometryWrote is the part of that write a trace or a window move made; m_tracedLanded marks traced probes
    // landed and not yet published.
    private bool m_wrote;
    private bool m_geometryWrote;
    private bool m_tracedLanded;
    // The published geometry's serial (SdfMeshIrradianceParameters.Geometry), and whether a landed retrace or a
    // republication owes it a step at the next publication that copies slots. It steps there and nowhere else, so a
    // gather kept under a serial never saw the slots a later step published.
    private uint m_geometrySerial = NextGeometrySerial();
    private bool m_geometryOwed;
    // The geometry serials taken across the process, so no two publications of any volumes share one.
    private static uint GeometrySerials;
    // The published windows' serial (SdfMeshIrradianceParameters.Windows): it steps with each publication that moves a
    // window without laying its slots out again.
    private uint m_windowSerial;
    private bool m_reported;
    private long m_traced;
    private long m_relit;
    private long m_rays;

    /// <summary>Gets the brightest luminance, in nits, of the sky the probes' lighting sees over its environment map;
    /// zero while no lighting is captured or the sky is no source of it.</summary>
    public float SkyPeakLuminance {
        get {
            lock (m_gate) {
                return ((m_lighting is null) ? 0f : m_skyPeak);
            }
        }
    }

    private float m_skyPeak;

    /// <summary>Gets whether probes are being traced or relit or wait to reach the volume table, or a camera sample is in
    /// flight: true throughout a lighting that changes every frame. A frame converging its lighting waits this out; a view
    /// does not, since what the volume publishes reaches it through its signature (each publication, and its exposures'
    /// air light and camera light).</summary>
    public bool Settling {
        get {
            lock (m_gate) {
                return SettlingTerms(names: null);
            }
        }
    }
    // Whether the volume is settling, naming each term that holds into `names` when given; under the gate. A faulted
    // worker, camera sample or scene build settles nothing: the next Stage reports it.
    private bool SettlingTerms(List<string>? names) {
        var settling = false;

        void Term(bool holds, string name) {
            if (!holds) { return; }
            settling = true;
            names?.Add(item: name);
        }
        Term(holds: (m_gpuCameraSampleTask is { IsCompleted: false } or { IsFaulted: true }), name: Named(task: m_gpuCameraSampleTask, name: "camera-sample"));
        Term(holds: (m_worker is { IsCompleted: false } or { IsFaulted: true }), name: Named(task: m_worker, name: (((names is not null) && (m_gpuTrace?.DescribeWait() is { } wait)) ? $"worker-{wait}" : "worker")));
        Term(holds: (m_build is { IsCompleted: false } or { IsFaulted: true }), name: Named(task: m_build, name: "build"));
        Term(holds: ((m_scene is not null) && m_owed), name: "owed");
        Term(holds: m_refillOwed, name: "refill");
        Term(holds: Dirty, name: (m_republishOwed ? "unpublished-republish-owed" : "unpublished"));
        Term(holds: m_wrote, name: "wrote");
        return settling;

        // A task's term, naming its fault where it faulted; only a description asks.
        string Named(Task? task, string name) => (((names is not null) && (task is { IsFaulted: true } faulted))
            ? $"{name}-faulted:{faulted.Exception?.GetBaseException().GetType().Name}:{faulted.Exception?.GetBaseException().Message}"
            : name);
    }
    // The settling terms that hold, by name, or none; under the gate.
    private string DescribeSettling() {
        var names = new List<string>();

        return (SettlingTerms(names: names) ? string.Join(separator: ',', values: names) : "none");
    }

    /// <summary>Gets whether the volume's background work is computing on the host: a scene build runs, or the worker runs
    /// while its probe service waits on no answer from the device. A frame driven while this holds advances nothing the
    /// work waits for, so a device-free driver waits this out before its next frame. Safe to read from any thread.</summary>
    public bool HostWorking {
        get {
            lock (m_gate) {
                return ((m_build is { IsCompleted: false }) || ((m_worker is { IsCompleted: false }) && !(m_gpuTrace?.AwaitsDevice ?? false)));
            }
        }
    }
    /// <summary>Gets whether the volume's light has converged for the lighting it is given: it is not settling at all
    /// (<see cref="Settling"/>), or, under a lighting that changes every frame (a moving sun, a keyed sky), its geometry is
    /// settled (no scene build, trace or window refill owed and every traced probe written) and the latest relight cycle
    /// kept every level's oldest probe within its age bound, so the light it shows trails the lighting by no more than
    /// the budget allows. <see langword="false"/> before a relight cycle has been measured under a changing lighting.</summary>
    public bool Converged {
        get {
            lock (m_gate) {
                return !Settling || (!GeometrySettling && (m_cycleStart != 0L) && !m_behind);
            }
        }
    }

    // Whether the latest relight cycle (Measure) found a level whose oldest probe exceeds its age bound.
    private bool m_behind;

    // Whether the published geometry is still changing: a scene build, a trace or a window's refill owed. A relight in
    // progress leaves the probes' cells, origins and surface maps as they stand.
    private bool GeometrySettling =>
        ((m_worker is { IsFaulted: true }) || (m_build is { IsCompleted: false } or { IsFaulted: true }) || ((m_scene is not null) && m_traceOwed) || m_refillOwed || Unwritten ||
        m_tracedLanded || m_geometryWrote);
    private bool Unwritten {
        get {
            foreach (var level in m_levels) {
                if (level.Unwritten > 0) { return true; }
            }
            return false;
        }
    }

    /// <summary>Advances the volume for a frame of the mesh path before the frame's upload: rebuilds the scene when what
    /// it holds for the static draws changed (each draw's trace mesh, <see cref="SdfMeshDraw.Trace"/>, else its drawn
    /// mesh), paced by the installed scene's trace progress and installed between the worker's batches, captures a
    /// changed lighting, grants the frame's budget, moves each level's window with the views' cameras, starts the worker
    /// on the probes the windows lack or whose lighting is stale, and writes the probes that landed into the tables'
    /// volume table.</summary>
    /// <param name="frame">The frame, its baked draws dressed in.</param>
    /// <param name="tables">The residency's tables.</param>
    /// <param name="tier">The indirect tier the views asked for: High measures the fine volume, Medium and Off the coarse
    /// one (Off applies none of its light, <see cref="SdfIndirectTier.Off"/>).</param>
    /// <param name="bakePending">Whether the mesh bake still owes opaque static meshes: a lighting-converged frame then builds no
    /// scene, and a frame whose bake has landed nothing builds none.</param>
    /// <returns>The parameters the views pass reads.</returns>
    public SdfMeshIrradianceParameters Stage(SdfFrame frame, SdfWorldTables tables, SdfIndirectTier tier, bool bakePending) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        // The writer is one delegate to a method of this instance, so a steady frame allocates nothing here.
        m_writeTables = tables;
        m_write ??= WriteToTables;

        return Stage(bakePending: bakePending, frame: frame, tier: tier, write: m_write);
    }

    // The residency attaches its probe service before it stages (EnsureTables, then Stage's reattach), so a volume that
    // publishes into tables copies the published slots from the service's feedback table on the device.
    private void WriteToTables(ReadOnlyMemory<uint> words) {
        var gpu = (GpuTrace ?? throw new InvalidOperationException(message: "The irradiance volume publishes into a residency's tables only through its probe service."));

        m_writeTables!.PublishMeshIrradiance(gpu.FeedbackBuffer, words.Length, m_changedSlots);
    }
    // The camera the window follows: the first view's. While another residency renders that view
    // (SdfViewSnapshot.Elsewhere: its seat is presented in another world, its camera in that world's coordinates) the
    // window holds where it stood, about where the seat left this world and will come back to it.
    private Vector3 WindowCamera(SdfFrame frame) => ((frame.Views[0].Elsewhere && m_windowValid)
        ? m_camera
        : frame.Views[0].Camera.Position);

    /// <summary>Advances the volume for a frame as <see cref="Stage(SdfFrame, SdfWorldTables, SdfIndirectTier, bool)"/>
    /// does, writing the volume table through a delegate rather than a residency's tables.</summary>
    /// <param name="frame">The frame, its baked draws dressed in.</param>
    /// <param name="tier">The indirect tier the views asked for: High measures the fine volume, Medium and Off the coarse
    /// one (Off applies none of its light, <see cref="SdfIndirectTier.Off"/>).</param>
    /// <param name="bakePending">Whether the mesh bake still owes opaque static meshes.</param>
    /// <param name="write">Receives the volume's probe slots, <see cref="SdfMeshIrradianceProbe.ProbeWords"/> words each,
    /// every level's after the finer level's, whenever a frame publishes a change to them.</param>
    /// <returns>The parameters the views pass reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="write"/> is
    /// <see langword="null"/>.</exception>
    public SdfMeshIrradianceParameters Stage(SdfFrame frame, SdfIndirectTier tier, bool bakePending, Action<ReadOnlyMemory<uint>> write) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: write);

        RayQueries = frame.RayQueries;
        if (frame.Views.Count == 0) { return default; }

        lock (m_gate) {
            if (m_gpuReattach) {
                // A replaced device stops its jobs first. Let their coordinator unwind before resetting its allocations.
                if ((m_worker is { IsCompleted: false }) || (m_gpuCameraSampleTask is { IsCompleted: false })) { return default; }
                m_gpuTrace = m_nextGpuTrace;
                m_gpuReattach = false;
                // The old device's camera sample is complete; a fault it left belonged to that device.
                m_gpuCameraSampleTask = null;
                m_gpuCameraSample = null;
                m_gpuCameraSampleAskedOf = null;
                // The new tables hold no table and the new service no feedback.
                ForgetPublication();
            }
        }

        // A lighting-converged frame's own work (its scene, its windows' refill, the background trace's yield and the
        // drain) runs on the thread pool, and the frame waits for each through the wait door under one deadline counted
        // from the convergence's first wait. On the host loop's pump the door clips every wait to what the iteration's
        // allowance has left, so a frame whose work outlasts it returns at once, Settling, and its residency answers
        // waiting with Convergence while the loop holds the tick; the next frame resumes the same work. Elsewhere the
        // frame waits for its work up to the deadline.
        var converging = frame.ConvergesLighting;
        var deadline = (converging ? ConvergenceDeadline() : default);

        // A lighting-converged frame must trace its own scene, not whichever asynchronous build happened to land first.
        if (converging && !bakePending && !AwaitScene(deadline: deadline, frame: frame)) {
            return Converging(stage: SdfMeshConvergenceStage.Scene);
        }

        var layout = Layout.Of(tier: tier, bounces: frame.IndirectBounces, levels: frame.IndirectLevels, reach: MathF.Max(x: 1f, y: frame.FarDistance));
        bool drain;

        lock (m_gate) {
            m_wrote = false;
            m_geometryWrote = false;
            var resizing = (!m_windowValid || (layout.Levels != m_layout.Levels));
            // Another finest level lays the volume out again, since only the measured levels hold slots. The levels both
            // layouts measure keep their caches, which the refill writes into their new slots. A trace or a camera
            // sample in flight reads the slots of the layout it was listed under, so the frame keeps that layout until
            // they land.
            var relaid = (!resizing && (layout.Finest != m_layout.Finest));

            if (relaid && ((m_worker is { IsCompleted: false }) || (m_gpuCameraSampleTask is { IsCompleted: false }))) {
                layout = (layout with { Finest = m_layout.Finest });
                relaid = false;
            }
            if ((resizing || relaid) && !TryReplaceVolume(layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords)) {
                return Converging(stage: converging ? SdfMeshConvergenceStage.Refill : SdfMeshConvergenceStage.None);
            }
            if (relaid) { ForgetPublication(); }
            if (ScenePendingDue(bakePending: bakePending, frame: frame)) { _ = RefreshScene(bakePending: bakePending, frame: frame); }
            RefreshLighting(frame: frame, layout: layout);
            Grant(budget: frame.IndirectBudget);
            if (!m_owed && m_nextLightCheck != 0 && Time.GetTimestamp() >= m_nextLightCheck) { Owe(); }
            m_camera = WindowCamera(frame: frame);
            CaptureViewCameras(frame: frame);
            if (m_cameraSamples.Length != frame.Views.Count) {
                m_cameraSamples = new CameraSample?[frame.Views.Count];
                m_nextCameraSample = 0;
            }

            if (resizing) {
                m_changedSlots.Clear();
                foreach (var level in m_levels) {
                    level.Cache.Clear();
                    level.Changed.Clear();
                    level.Published = default;
                }
                m_published = default;
            }
            if (resizing || (layout.Reach != m_layout.Reach)) { AdvanceGeometry(); }

            // A level the views come to read is refilled and listed as a moved window is: its slots may hold another
            // cell's probe, or none.
            var moved = (resizing || (layout.Finest != m_layout.Finest));
            var finest = m_layout.Finest;

            m_layout = layout;
            // The slots of a level the views read again reach the tables whole: a withdrawn level's changes were dropped.
            for (var level = layout.Finest; (!resizing && (level < finest)); level++) { OweSlots(level: level); }

            foreach (var level in m_levels) {
                var low = WindowLow(current: level.Low, frame: frame, layout: layout, level: level.Index, resizing: resizing);

                if (resizing || (low != level.Low)) {
                    level.Low = low;
                    moved = true;

                    var (residentLow, residentHigh) = Resident(dims: layout.DimsOf(level: level.Index), low: low);

                    level.Cache.Retain(high: residentHigh, low: residentLow);
                }
            }
            if (moved) {
                m_windowValid = true;
                Owe();
                m_traceOwed = true;
                OweRefill();
            }
            drain = (converging && (m_scene is not null) && (m_lighting is not null) && (m_owed || (m_drained != DrainKey())));
        }

        // A lighting-converged frame renders the window refilled, then traced and relit to the end, so its indirect light
        // and its views' exposure meters never depend on how far asynchronous work had got by the time the frame was
        // asked for, nor on the bounce earlier frames left in the cache. Every wait runs outside the gate, which the work
        // waited for takes to land.
        var stage = SdfMeshConvergenceStage.None;

        if (converging && !AwaitRefill(deadline: deadline)) { stage = SdfMeshConvergenceStage.Refill; }
        if (drain && (stage == SdfMeshConvergenceStage.None) && !AwaitYield(deadline: deadline)) { stage = SdfMeshConvergenceStage.Yield; }

        Task? draining = null;

        lock (m_gate) {
            if (m_worker is { IsFaulted: true } faulted) {
                m_worker = null;
                throw new InvalidOperationException(message: "The irradiance volume's trace failed.", innerException: faulted.Exception);
            }
            if (m_gpuCameraSampleTask is { IsFaulted: true } cameraSampleFault) {
                m_gpuCameraSampleTask = null;
                throw new InvalidOperationException(message: "The GPU exposure meter failed.", innerException: cameraSampleFault.Exception);
            }
            // A converging frame still waiting for its refill or the yield starts no worker: the drain takes over the
            // background trace's work, and starts only from the windows it renders.
            if (!converging || (stage == SdfMeshConvergenceStage.None)) {
                if (drain && !m_draining) { m_yield = true; }
                if ((m_owed || drain) && (m_scene is not null) && (m_lighting is not null) && (m_worker is null or { IsCompleted: true }) && (Granted || drain)) {
                    m_yield = false;
                    m_draining = drain;
                    if (drain) { (m_drainPass, m_drainDone, m_drainTotal) = (0, 0, 0); }
                    m_worker = StartWorker(drain: drain);
                }
                if (drain && m_draining) { draining = m_worker; }
            }
        }
        if ((draining is not null) && !AwaitWorker(deadline: deadline, reason: in DrainWait, worker: draining)) { stage = SdfMeshConvergenceStage.Drain; }
        lock (m_gate) {
            // A drain that stopped for a change of what it drew from leaves the frame's drain owed, which the next
            // frame starts afresh.
            if (drain && (stage == SdfMeshConvergenceStage.None) && (m_owed || (m_drained != DrainKey()))) { stage = SdfMeshConvergenceStage.Drain; }
            Publish(layout: layout, write: write);
            Volatile.Write(location: ref m_owesFirstLight, value: OwesFirstLightOf(layout: layout, seen: ((frame.DisplayShare > 0f) && (tier != SdfIndirectTier.Off))));
        }
        ScheduleGpuCameraSamples(frame: frame);
        // A frame that does not converge lighting ends any convergence one left behind.
        return Converging(stage: stage);
    }

    // Under the volume gate. Keep the published table until its complete replacement fits.
    // Under the gate: forgets what the device holds of the volume (its hit slots, its snapshot, the published levels),
    // for a probe service whose feedback no longer holds the volume's words in their slots: a new service (new tables),
    // or a volume laid out again. The volume's words still hold every measured slot, so with a probe service the first
    // relight's full snapshot carries them into the feedback, and then every slot republishes at once (RepublishAll)
    // rather than a level publishing slots it never copied; without one, the refill owes every slot to the next write.
    private void ForgetPublication() {
        foreach (var owner in m_hitOwners.Values.OrderByDescending(keySelector: owner => owner.Slot)) { m_hitSlots.Free(start: owner.Slot); }
        m_hitOwners.Clear();
        m_gpuVolume = null;
        m_gpuDirty.Clear();
        m_listed = false;
        m_drained = default;
        m_changedSlots.Clear();
        foreach (var level in m_levels) { level.Published = default; level.Changed.Clear(); }
        m_published = default;
        m_republishOwed = (m_gpuTrace is not null);
        Owe();
        m_traceOwed = true;
    }
    // Under the gate. A refusal keeps the volume standing.
    private bool TryReplaceVolume(int words) {
        VolumeWords next;

        try { next = new VolumeWords(allocator: m_allocator, owner: m_volumeOwner, words: words); }
        catch (HostMemoryRefusedException) { return false; }
        RetireVolume(next: next);
        return true;
    }
    // Under the gate: publishes the next words and retires the replaced ones to the reader epochs, so a reader that took
    // them under a hold keeps reading them until it ends.
    private void RetireVolume(VolumeWords next) {
        var old = m_volume;

        m_volume = next;
        if (!ReferenceEquals(objA: old, objB: VolumeWords.None)) { m_blocks.Retire(item: old); }
    }
    // Starts the trace worker: the drain of a lighting-converged frame, or the budgeted background work. Apart from
    // Stage, whose every call would otherwise allocate the closure over `drain`.
    private Task StartWorker(bool drain) => Task.Run(action: () => {
        try { if (drain) { Drain(); } else { Work(); } } catch (OperationCanceledException) when ((GpuTrace is not null)) { m_listed = false; }
    });

    private static readonly WaitReason SceneWait = new(Subsystem: SourceName, WaitsFor: "a converging frame's trace scene");
    private static readonly WaitReason RefillWait = new(Subsystem: SourceName, WaitsFor: "a converging frame's window refill");
    private static readonly WaitReason YieldWait = new(Subsystem: SourceName, WaitsFor: "the background trace yielding to a converging frame");
    private static readonly WaitReason DrainWait = new(Subsystem: SourceName, WaitsFor: "a converging frame's drain");
    // How long a lighting-converged frame's waits may take in all, counted from its convergence's first wait and never
    // restarted by a later frame: off the pump, the longest a caller blocks for one convergence. Its expiry gives up
    // nothing: a frame past it answers waiting at once until its work lands, and a debug world's whole convergence on
    // the pump can outlast it. Presentation only.
    private static readonly TimeSpan ConvergenceStall = TimeSpan.FromMinutes(value: 2d);

    // The convergence's first wait as a Stopwatch timestamp, zero while none stands, the frame thread's; and where it
    // stands, as Convergence reports it, under the gate.
    private long m_convergingSince;
    private SdfMeshConvergenceStage m_convergence;
    // The drain's progress: its pass, the probes the pass has landed and the probes a pass takes. The drain's, under the gate.
    private int m_drainPass;
    private int m_drainDone;
    private int m_drainTotal;
    // Whether the latest worker started is a drain, so a frame that asks for one does not ask the drain to yield.
    private bool m_draining;

    /// <summary>Gets where the latest lighting-converged frame's own work stands: the part its held frame still waits
    /// for and the drain's progress; <see cref="SdfMeshConvergenceStage.None"/> once it has landed, and for a frame
    /// that does not converge lighting. Safe to read from any thread.</summary>
    public SdfMeshConvergence Convergence {
        get {
            lock (m_gate) {
                return ((m_convergence == SdfMeshConvergenceStage.Drain)
                    ? new SdfMeshConvergence(Done: m_drainDone, Pass: m_drainPass, Stage: m_convergence, Total: m_drainTotal)
                    : new SdfMeshConvergence(Done: 0, Pass: 0, Stage: m_convergence, Total: 0));
            }
        }
    }

    // The deadline of the standing convergence's waits, starting one when none stands.
    private WaitDeadline ConvergenceDeadline() {
        if (m_convergingSince == 0L) { m_convergingSince = Stopwatch.GetTimestamp(); }
        return WaitDeadline.At(timestamp: m_convergingSince.AddSaturating(right: WaitDeadline.TicksOf(budget: ConvergenceStall)));
    }
    // Records where a converging frame's work stands, ending the convergence once it has landed, and returns the
    // published parameters.
    private SdfMeshIrradianceParameters Converging(SdfMeshConvergenceStage stage) {
        lock (m_gate) { m_convergence = stage; }
        if (stage == SdfMeshConvergenceStage.None) { m_convergingSince = 0L; }
        return m_published;
    }
    // Whether this frame's static draws are the installed scene, waiting for a build in flight. A build already in flight
    // may belong to an older frame, so the frame resolves again after it lands until its own draws are installed; a build
    // that faulted fails the frame as it resolves (RefreshScene).
    private bool AwaitScene(SdfFrame frame, WaitDeadline deadline) {
        while (true) {
            Task? build;

            lock (m_gate) {
                if (m_disposed) { return true; }
                build = RefreshScene(frame: frame);
                if (build is null) { return !WaitingForMemory(m_sceneRefusedAt); }
            }
            if (HostWaits.Wait(deadline: deadline, reason: in SceneWait, task: build) == WaitOutcome.DeadlinePassed) { return false; }
        }
    }
    // Whether the windows' refill has landed, waiting for the one in flight. A refill that faulted leaves its refill
    // owed, so Settling holds the frame and every converging frame fails with it here.
    private bool AwaitRefill(WaitDeadline deadline) {
        Task? refill;

        lock (m_gate) { refill = (m_refillOwed ? m_refill : null); }
        if (refill is null) { return true; }
        if (HostWaits.Wait(deadline: deadline, reason: in RefillWait, task: refill) == WaitOutcome.DeadlinePassed) { return false; }
        if (refill.IsFaulted) { throw new InvalidOperationException(message: "The irradiance volume's window refill failed.", innerException: refill.Exception); }
        return true;
    }
    // Whether no background trace runs, asking the one in flight to return at its next batch and waiting for it, so the
    // drain takes over whatever budgeted work it had left. A faulted worker is reported as any frame reports it.
    private bool AwaitYield(WaitDeadline deadline) {
        Task? worker;

        lock (m_gate) {
            if (m_draining || (m_worker is not { IsCompleted: false } running)) { return true; }
            m_yield = true;
            worker = running;
        }
        return AwaitWorker(deadline: deadline, reason: in YieldWait, worker: worker);
    }
    // Whether a worker has finished, waiting for a CPU trace's. A GPU trace's jobs advance only as the frame pumps its
    // residency's GPU work, after this staging, so the frame never waits for that worker: it reads whether it finished.
    private bool AwaitWorker(Task worker, in WaitReason reason, WaitDeadline deadline) =>
        ((GpuTrace is null) ? (HostWaits.Wait(deadline: deadline, reason: in reason, task: worker) == WaitOutcome.Completed) : worker.IsCompleted);
    // What a drain's light is a function of: the geometry, the lighting, the windows and the layout.
    private (int Geometry, int Lighting, int Windows, Layout Layout) DrainKey() => (m_geometry, m_lightingVersion, m_windowVersion, m_layout);

    // Whether the worker may do any work this frame: an unbounded part of the budget, or a whole probe's credit left in
    // either part.
    private bool Granted => ((m_budget.RelightPerFrame <= 0) || (m_budget.TracePerFrame <= 0) || (m_relightCredits >= 1.0) || (m_traceCredits >= 1.0));

    /// <summary>The frames of a part's grant the worker may hold unspent: a quarter second at 60 Hz, so a worker that
    /// falls behind banks no burst past it.</summary>
    public const int MostBankedFrames = 15;

    // Grants the budget once for each frame the volume stages: each bounded part gains its count a frame and holds at most
    // MostBankedFrames of it. The grant is the frame's, never the wall time's, so a frame that runs long owes no more
    // probe work than a short one and a slow device does less work a second, which is the direction that lets it catch
    // up. Presentation only: the drain of a lighting-converged frame ignores the budget, so its light never depends on
    // the frame count.
    private void Grant(SdfIndirectBudget budget) {
        budget.Validate();
        m_budget = budget;

        if (budget.RelightPerFrame > 0) { m_relightCredits = Banked(credits: m_relightCredits, perFrame: budget.RelightPerFrame); }
        if (budget.TracePerFrame > 0) { m_traceCredits = Banked(credits: m_traceCredits, perFrame: budget.TracePerFrame); }
    }
    // A part's credits after one frame's grant: at most MostBankedFrames of it, and never less than a whole probe's worth of
    // room, so a count that leaves a fraction of a probe still reaches one.
    private static double Banked(double credits, int perFrame) =>
        Math.Min(val1: (credits + perFrame), val2: Math.Max(val1: 1.0, val2: (((double)perFrame) * MostBankedFrames)));
    // Owes the worker work; the clock the done line reports starts when the volume leaves its settled state.
    private void Owe() {
        m_nextLightCheck = 0;
        m_sweepsSince = 0;
        m_unconvergedReported = false;
        if (!m_owed) {
            m_clock.Restart();
            m_traced = 0;
            m_relit = 0;
            m_rays = 0;
        }
        m_owed = true;
        m_reported = false;
    }
    // A new geometry: the probes it may change trace again, each keeping its previous measurement for views until its
    // trace lands. `changed` holds the hierarchy of the world boxes of the static draws the new scene added or removed;
    // null changes every probe (a layout or reach change, or a draw with no box).
    private void AdvanceGeometry(ChangeTree? changed = null) {
        m_geometry++;
        m_changes.Add(item: (m_geometry, changed));
        if (m_changes.Count > MaxChanges) { m_changes.RemoveAt(index: 0); }
        Owe();
        m_traceOwed = true;
    }
    // The hierarchy over the world boxes of the static draws one static draw list holds and the other does not, counted
    // as multisets of their mesh, transform and material (ChangeTree); null when one of them has no box. The build's
    // alone: it counts in workspace kept across builds.
    private ChangeTree? ChangedBoxes(ReadOnlySpan<SdfMeshDraw> before, ReadOnlySpan<SdfMeshDraw> after) {
        var counts = m_changeCounts;
        var changed = m_changeBoxes;

        try {
            foreach (ref readonly var draw in before) {
                if (!HasComparableTransform(in draw)) { return null; }
                ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: counts, key: (draw.Mesh, draw.FixedPlacement, draw.Material), exists: out _);

                count++;
            }
            foreach (ref readonly var draw in after) {
                if (!HasComparableTransform(in draw)) { return null; }
                ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary: counts, key: (draw.Mesh, draw.FixedPlacement, draw.Material));

                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(source: ref count) && (count > 0)) {
                    count--;
                    continue;
                }
                if (!TryBox(box: out var box, draw: in draw)) { return null; }
                changed.Add(item: box);
            }
            foreach (ref readonly var draw in before) {
                ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary: counts, key: (draw.Mesh, draw.FixedPlacement, draw.Material));

                if (count <= 0) { continue; }
                count--;
                if (!TryBox(box: out var box, draw: in draw)) { return null; }
                changed.Add(item: box);
            }
            return ChangeTree.Of(boxes: changed);
        } finally {
            counts.Clear();
            changed.Clear();
        }
    }
    // Matrix4x4.Equals groups NaN entries. Invalid transforms cannot certify unchanged geometry.
    private static bool HasComparableTransform(in SdfMeshDraw draw) {
        var transform = draw.FixedPlacement;
        return transform == draw.FixedPlacement;
    }

    private static bool TryBox(in SdfMeshDraw draw, out Box box) {
        if (!SdfMeshBox.TryOfFixed(box: out var oriented, draw: in draw)) {
            box = default;
            return false;
        }

        var extent = oriented.AxisHalf;

        box = new Box(High: ((oriented.Center + extent) + new Vector3(value: SdfMeshIrradianceProbe.BoxMargin)), Low: ((oriented.Center - extent) - new Vector3(value: SdfMeshIrradianceProbe.BoxMargin)));
        return true;
    }
    // Publish completed slots together. Full cell coordinates reject old owners as soon as a window moves.
    // After a device reattach nothing publishes until RepublishAll has owed every slot.
    // A level the frame's views do not read (Layout.Measures) is withdrawn: its slots stay, and the views stop reading it.
    private void Publish(Layout layout, Action<ReadOnlyMemory<uint>> write) {
        foreach (var level in m_levels) {
            if (!layout.Measures(level: level.Index)) {
                level.Changed.Clear();
                if (level.Published.Present) {
                    level.Published = default;
                    m_wrote = true;
                }
                continue;
            }

            var window = layout.WindowOf(level: level.Index, low: level.Low);

            if (m_republishOwed || ((level.Changed.Count == 0) && (!level.Published.Present || (level.Published == window)))) { continue; }
            m_geometryWrote |= (m_tracedLanded || (level.Published != window));
            m_changedSlots.AddRange(source: level.Changed.Members);
            level.Changed.Clear();
            level.Published = window;
            m_wrote = true;
        }
        var copied = (m_changedSlots.Count > 0);

        if (copied) {
            m_tracedLanded = false;
            write(obj: m_volume.Memory[..(layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords)]);
            m_changedSlots.Clear();
        }

        var enabled = false;

        foreach (var level in m_levels) { enabled |= level.Published.Present; }

        var published = new SdfMeshIrradianceParameters(Enabled: enabled, Far: PublishedOf(layout: layout, level: 2), Finest: PublishedOf(layout: layout, level: 0),
            Middle: PublishedOf(layout: layout, level: 1), Geometry: m_geometrySerial, Windows: m_windowSerial);

        // A resized, respaced or withdrawn window lays every slot out again, and a copied retrace changes a held probe:
        // either steps the geometry serial with the publication that shows it. A window that only moved steps the window
        // serial alone: the slots of the cells it kept still hold them.
        var relaid = false;
        var moved = false;

        for (var level = 0; (level < SdfMeshIrradianceProbe.Levels); level++) {
            var now = published.Window(level: level);
            var before = m_published.Window(level: level);

            relaid |= ((now.Spacing != before.Spacing) || (now.Dims != before.Dims) || (now.Present != before.Present));
            moved |= (now.Low != before.Low);
        }
        relaid |= (published.Enabled != m_published.Enabled);
        if (relaid || (copied && m_geometryOwed)) {
            m_geometryOwed = (m_geometryOwed && !copied);
            m_geometrySerial = NextGeometrySerial();
        }
        if (moved) { m_windowSerial++; }
        m_published = published with { Geometry = m_geometrySerial, Windows = m_windowSerial };
    }
    // A geometry serial no publication of any volume has held, never zero.
    private static uint NextGeometrySerial() {
        uint serial;

        do {
            serial = Interlocked.Increment(location: ref GeometrySerials);
        } while (serial == 0u);
        return serial;
    }
    // Where a level's window centres for the first view. The finest level's anchor is its camera moved ahead along its
    // horizontal forward by a quarter of the window's horizontal extent, so the finest probes cover what the view looks
    // across rather than as much behind it, and the camera stays a quarter of the window inside its back face; a view
    // looking straight up or down keeps its camera. A coarser level centres on the camera: its window already reaches
    // past the finest one's on every side.
    private Vector3 WindowAnchor(SdfFrame frame, Layout layout, int level) {
        var camera = frame.Views[0].Camera;

        if ((level != 0) || frame.Views[0].Elsewhere) { return m_camera; }

        var ahead = new Vector3(x: camera.Forward.X, y: 0f, z: camera.Forward.Z);
        var length = ahead.Length();

        if (!(length > 1e-3f) || !float.IsFinite(f: length)) { return m_camera; }

        var extent = (layout.SpacingOf(level: level) * new Vector3(x: layout.DimsOf(level: level).X, y: 0f, z: layout.DimsOf(level: level).Z));

        return (m_camera + ((ahead / length) * (0.25f * MathF.Min(x: extent.X, y: extent.Z))));
    }
    /// <summary>Whether a displayed view still lacks a published cell at its camera during refill. The probe service
    /// takes the budget's owed allowance until the camera cells arrive, then refines under the regular display share.</summary>
    public bool OwesFirstLight => Volatile.Read(location: ref m_owesFirstLight);

    private bool m_owesFirstLight;

    // A view outside all three windows uses its camera sample. No available trace could fill that cell for it.
    private bool OwesFirstLightOf(Layout layout, bool seen) {
        if (!seen || m_scene is null || !Unwritten) { return false; }
        foreach (var camera in m_viewCameras) {
            var missing = false;
            for (var level = layout.Finest; level < m_levels.Length; level++) {
                var cell = SdfMeshLattice.CellOf(cell: layout.SpacingOf(level), point: camera.Position);
                if (!InWindow(level, cell)) { continue; }
                if (m_levels[level].Published.Present && Holds(level, cell)) { missing = false; break; }
                missing = true;
            }
            if (missing) { return true; }
        }
        return false;
    }
    // A level's published window; one not yet published carries its probe counts at zero spacing, so the views pass skips
    // it yet places the coarser levels' slots after its own.
    private SdfMeshIrradianceWindow PublishedOf(Layout layout, int level) => (m_levels[level].Published.Present
        ? m_levels[level].Published
        : new SdfMeshIrradianceWindow(Dims: layout.StoredDimsOf(level: level), Low: default, Spacing: Vector3.Zero));
    // The first cell of a level's window. It centres on the box of the cells of every view's camera this residency
    // renders, the first view's taken at its anchor ahead of it (WindowAnchor), and on the first view's anchor alone when
    // that box and the first camera do not fit in the window's middle together, so each level follows every view as far
    // as its reach allows and a coarser level covers views a finer one cannot. The first camera itself is always kept
    // in the middle, an eighth of the window short of each face: the window keeps its place only while every point it
    // keeps (the first camera, its anchor and the views that fit) stands in the current middle, and a recentred window
    // is shifted, where the centre leaves one out, until all stand in its middle. So a camera that moves back and forth
    // across a cell (a body falling through a floor painting and out of the ceiling's), or turns, keeps one window as
    // long as its anchor stays in the middle (a turn in place never moves the camera), and a camera walking backward
    // recentres before it nears the back face. The anchor and the camera always fit together: the anchor stands a
    // quarter of the horizontal extent ahead, inside the middle's three quarters.
    private (int X, int Y, int Z) WindowLow(SdfFrame frame, int level, Layout layout, (int X, int Y, int Z) current, bool resizing) {
        var spacing = layout.SpacingOf(level: level);
        var dims = layout.DimsOf(level: level);
        var anchor = SdfMeshLattice.CellOf(cell: spacing, point: WindowAnchor(frame: frame, layout: layout, level: level));
        var camera = SdfMeshLattice.CellOf(cell: spacing, point: m_camera);
        // The box the window centres on, and the box it keeps in its middle: the same with the first camera.
        var low = anchor;
        var high = anchor;
        var keptLow = Min(a: anchor, b: camera);
        var keptHigh = Max(a: anchor, b: camera);

        for (var index = 1; (index < frame.Views.Count); index++) {
            var view = frame.Views[index];
            var position = view.Camera.Position;

            if (view.Elsewhere || !float.IsFinite(f: position.X) || !float.IsFinite(f: position.Y) || !float.IsFinite(f: position.Z)) { continue; }

            var cell = SdfMeshLattice.CellOf(cell: spacing, point: position);

            low = Min(a: low, b: cell);
            high = Max(a: high, b: cell);
        }
        if (Fits(dims: dims.X, high: Math.Max(val1: high.X, val2: keptHigh.X), low: Math.Min(val1: low.X, val2: keptLow.X)) &&
            Fits(dims: dims.Y, high: Math.Max(val1: high.Y, val2: keptHigh.Y), low: Math.Min(val1: low.Y, val2: keptLow.Y)) &&
            Fits(dims: dims.Z, high: Math.Max(val1: high.Z, val2: keptHigh.Z), low: Math.Min(val1: low.Z, val2: keptLow.Z))) {
            keptLow = Min(a: keptLow, b: low);
            keptHigh = Max(a: keptHigh, b: high);
        } else {
            low = anchor;
            high = anchor;
        }
        if (!resizing && InMiddle(cell: keptLow, dims: dims, low: current) && InMiddle(cell: keptHigh, dims: dims, low: current)) {
            return current;
        }

        return (Centre(dims: dims.X, high: high.X, keptHigh: keptHigh.X, keptLow: keptLow.X, low: low.X),
            Centre(dims: dims.Y, high: high.Y, keptHigh: keptHigh.Y, keptLow: keptLow.Y, low: low.Y),
            Centre(dims: dims.Z, high: high.Z, keptHigh: keptHigh.Z, keptLow: keptLow.Z, low: low.Z));
    }
    // A window's first cell along one axis centred on cells low to high, then shifted the least that puts keptLow to
    // keptHigh in its middle (Middle); a kept box that fits (Fits) always has such a place, so the window then stands.
    private static int Centre(int low, int high, int keptLow, int keptHigh, int dims) {
        var margin = MarginOf(dims: dims);

        return Math.Min(val1: Math.Max(val1: (((low + high) >> 1) - (dims / 2)), val2: (((keptHigh - dims) + margin) + 1)), val2: (keptLow - margin));
    }
    private static (int X, int Y, int Z) Min((int X, int Y, int Z) a, (int X, int Y, int Z) b) =>
        (Math.Min(val1: a.X, val2: b.X), Math.Min(val1: a.Y, val2: b.Y), Math.Min(val1: a.Z, val2: b.Z));
    private static (int X, int Y, int Z) Max((int X, int Y, int Z) a, (int X, int Y, int Z) b) =>
        (Math.Max(val1: a.X, val2: b.X), Math.Max(val1: a.Y, val2: b.Y), Math.Max(val1: a.Z, val2: b.Z));
    // Whether cells from low to high fit in a window's middle along one axis.
    private static bool Fits(int low, int high, int dims) => ((high - low) < (dims - (2 * MarginOf(dims: dims))));

    /// <summary>Samples the volume at a camera, as one probe there: the light the probes about the camera see along each
    /// of their six axes, as last relit, the light the surfaces about it reflect and emit under every traced light and the
    /// sky through the openings it sees, blended by the camera's place among the visible probes. When no visible lattice
    /// probe answers, reads a camera sample's hits, traced once per cell and geometry and relit when the lighting changes.
    /// Reuse within a cell requires visibility from the sample's origin. On a device the sample is its camera sample's latest
    /// answer, and until the device first answers, the camera's own trace on the built scene. An anchored light and the
    /// screens are no part of the sample. The views read it for a surface no probe answers and for the sky the air about the camera sees; the
    /// exposure meters the view's image, never this sample.</summary>
    /// <param name="position">The camera's position, in world units.</param>
    /// <param name="skyVisibility">The share of the sky the probes about the camera see open, alike in every direction,
    /// in [0, 1], when the method returns <see langword="true"/>: one on open ground, whose probes see the sky over the
    /// upper half of their directions, near zero in a closed room.</param>
    /// <param name="source">Whether the sample came from lattice probes, a camera trace, or neither.</param>
    /// <param name="fresh">Whether the sample must be the camera's own: at a camera cut, a camera sample is reused only
    /// from this very position, never from elsewhere in its cell, so the sample depends on where the camera stands and
    /// not on where it stood before.</param>
    /// <param name="light">Receives, when not empty and the method returns <see langword="true"/>, the light the sample
    /// receives along each of its six axes (+X, -X, +Y, -Y, +Z, -Z), RGB in nits: the radiance and sky-light cubes summed,
    /// as one probe's indirect light. At least six long when not empty.</param>
    /// <returns><see langword="false"/> while no scene is available or the camera lies inside opaque geometry.</returns>
    public bool TrySampleCamera(Vector3 position, out float skyVisibility, out SdfCameraSampleSource source, bool fresh = false, Span<Vector3> light = default) {
        skyVisibility = 1f;
        source = SdfCameraSampleSource.None;
        Counters.Count(kind: CameraSamples);

        lock (m_gate) {
            if (GpuTrace is { } gpu) {
                if (TryGpuCameraSample(fresh: fresh, light: light, position: position, skyVisibility: out skyVisibility, source: out source)) { return true; }
                // The device samples the camera only once it holds the volume's scene, which it uploads after the scene is built and
                // beside the residency's mesh landing, seconds into a boot. Until its first answer the camera traces its
                // own hits on the built scene, so a view's open sky and surroundings are known from the build on; after
                // it, a view holds its last sample for the frames a camera sample takes.
                if ((m_gpuCameraSample is { } answered) && ReferenceEquals(objA: answered.Client, objB: gpu)) { return false; }
                if ((m_lighting is not { } early) || !m_windowValid) { return false; }
                return TryTraceCamera(fresh: fresh, light: light, lighting: early, position: position, skyVisibility: out skyVisibility, source: out source);
            }
            if ((m_lighting is not { } lighting) || !m_windowValid) {
                return false;
            }

            var spacing = m_layout.SpacingOf(m_layout.Finest);
            var cache = m_levels[m_layout.Finest].Cache;
            using var hold = m_blocks.Enter();
            var sample = ((position / spacing) - new Vector3(value: 0.5f));
            var floor = new Vector3(x: MathF.Floor(x: sample.X), y: MathF.Floor(x: sample.Y), z: MathF.Floor(x: sample.Z));
            var fraction = (sample - floor);
            Span<Vector3> axes = stackalloc Vector3[6];
            var open = 0f;
            var total = 0f;

            for (var corner = 0; (corner < 8); corner++) {
                var offset = new Vector3(x: corner & 1, y: (corner >> 1) & 1, z: (corner >> 2) & 1);
                var cell = (((int)(floor.X + offset.X)), ((int)(floor.Y + offset.Y)), ((int)(floor.Z + offset.Z)));

                if (!cache.TryGetValue(key: cell, value: out var probe, hold: in hold) || probe.Invalid) {
                    continue;
                }

                var probePoint = ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * spacing) + probe.Offset);
                var toward = (probePoint - position);
                var distance = toward.Length();

                if ((distance > SdfMeshIrradianceProbe.CameraSampleCoincident) && (m_scene is { } scene) && !scene.Unblocked(direction: (toward / distance), maximum: distance, origin: position)) { continue; }

                var weight = ((((offset.X > 0f) ? fraction.X : (1f - fraction.X)) * ((offset.Y > 0f) ? fraction.Y : (1f - fraction.Y))) * ((offset.Z > 0f) ? fraction.Z : (1f - fraction.Z)));

                if (weight <= 0f) {
                    continue;
                }

                var sky = probe.Sky;
                var radiance = probe.Radiance;
                var skyLight = probe.SkyLight;

                for (var axis = 0; (axis < 6); axis++) {
                    axes[axis] += ((radiance[axis] + skyLight[axis]) * weight);
                    open += ((sky[axis] * weight) / 6f);
                }
                total += weight;
            }

            if (total <= 0f) { return TryTraceCamera(fresh: fresh, light: light, lighting: lighting, position: position, skyVisibility: out skyVisibility, source: out source); }
            for (var axis = 0; (axis < 6); axis++) { axes[axis] /= total; }
            skyVisibility = Math.Clamp(max: 1f, min: 0f, value: ((SdfMeshIrradianceProbe.OpenGroundShare * open) / total));
            if (!light.IsEmpty) { axes.CopyTo(destination: light); }
            source = SdfCameraSampleSource.Probes;

            return true;
        }
    }

    // Samples the camera by its own hits on the volume's scene (CameraRadiance), as one probe at the camera, which needs no
    // lattice probe and no device; under the gate. False before the scene is built or inside opaque geometry.
    private bool TryTraceCamera(Vector3 position, Lighting lighting, bool fresh, Span<Vector3> light, out float skyVisibility, out SdfCameraSampleSource source) {
        skyVisibility = 1f;
        source = SdfCameraSampleSource.None;
        if (m_scene is not { } scene) { return false; }

        var sample = CameraRadiance(fresh: fresh, lighting: lighting, position: position, scene: scene);

        if (sample.Invalid) { return false; }

        var open = 0f;

        for (var axis = 0; (axis < 6); axis++) {
            if (!light.IsEmpty) { light[axis] = (sample.Radiance[axis] + sample.SkyLight[axis]); }
            open += (sample.Sky[axis] / 6f);
        }
        skyVisibility = Math.Clamp(max: 1f, min: 0f, value: (SdfMeshIrradianceProbe.OpenGroundShare * open));
        source = SdfCameraSampleSource.CameraTrace;

        return true;
    }
    // A camera sample's hits are traced once per cell and geometry, at the camera's own position; its cubes are relit
    // from them when the lighting changes or the volume settles, whose bounce they read. A cell can straddle a partition:
    // a sample belongs to its actual origin, never an assumed open cell centre, and is never written into the volume.
    private CameraSample CameraRadiance(Vector3 position, SdfMeshIrradianceScene scene, Lighting lighting, bool fresh) {
        var cell = SdfMeshLattice.CellOf(cell: m_layout.Spacing, point: position);
        var lows = Lows();

        foreach (var sample in m_cameraSamples) {
            if ((sample is null) || (sample.Geometry != m_geometry) || (sample.Cell != cell)) { continue; }

            var reuse = (sample.Position == position);

            if (!reuse && !fresh && !sample.Invalid) {
                var toward = (position - sample.Position);
                var distance = toward.Length();

                reuse = scene.Unblocked(direction: (toward / distance), maximum: distance, origin: sample.Position);
            }
            if (!reuse) { continue; }
            if (!sample.Invalid && ((sample.Lighting != lighting.Version) || (sample.Settled != !m_owed))) {
                // The hits were traced from the sample's own origin, so it is relit there, however far within its cell the
                // camera now stands.
                RelightCameraSample(lighting: lighting, lows: lows, reuseLocal: (sample.LocalVersion == lighting.LocalVersion), sample: sample, scene: scene);
                sample.Lighting = lighting.Version;
                sample.LocalVersion = lighting.LocalVersion;
                sample.Settled = !m_owed;
                Counters.Count(kind: CameraRelights);
            }
            return sample;
        }

        var traced = new CameraSample(Cell: cell, Geometry: m_geometry, Position: position);
        var valid = TraceHits(hits: traced.Hits, inside: out _, layout: m_layout, origin: position, scene: scene, sky: traced.Sky);

        traced.Invalid = !valid;
        if (valid) { RelightCameraSample(lighting: lighting, lows: lows, reuseLocal: false, sample: traced, scene: scene); }
        traced.Lighting = lighting.Version;
        traced.LocalVersion = lighting.LocalVersion;
        traced.Settled = !m_owed;
        Counters.Count(kind: CameraTraces);
        m_cameraSamples[m_nextCameraSample] = traced;
        m_nextCameraSample = ((m_nextCameraSample + 1) % m_cameraSamples.Length);
        return traced;
    }
    // Relights a camera sample's hits from its own origin and closes them (CloseCameraSample).
    private void RelightCameraSample(CameraSample sample, Lighting lighting, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene, bool reuseLocal) {
        Span<Vector3> open = stackalloc Vector3[SdfMeshProbeDepth.HitCount];
        using var hold = m_blocks.Enter();

        Relight(feedback: (lighting.Feedback && m_layout.Bounce), hits: sample.Hits, layout: m_layout, lighting: lighting, lights: default, local: sample.Local,
            lows: lows, origin: sample.Position, radiance: sample.Radiance, reuseLocal: reuseLocal, scene: scene, sky: sample.SkyLight, open: open, hold: in hold);
        CloseCameraSample(hits: sample.Hits, open: open, radiance: sample.Radiance, sky: sample.SkyLight);
    }
    // KEEP IN SYNC with the closure in sdf-probe-camera-sample-stage.hlsli. A camera sample stands for every surface no probe
    // answers (sdfMeshIndirectSeen), so a hit of its own that no probe answers reflects the light the sample receives about
    // the hit's normal: one probe's interreflection, iterated CameraSampleClosurePasses times from the relit cube. Without
    // it, before the volume measures a room, its walls carry only the fixtures' direct light while an opening shows the
    // daylit ground outside at full strength, and every unanswered surface in the room takes the opening's colour. `open`
    // is each texel's reflectance its bounce leaves open (Shade's Open); `sky` stays as relit.
    private static void CloseCameraSample(ReadOnlySpan<uint> hits, ReadOnlySpan<Vector3> open, Span<Vector3> radiance, ReadOnlySpan<Vector3> sky) {
        var directions = SdfMeshProbeDepth.RayDirections;
        var weights = SdfMeshProbeDepth.RayWeights;
        Span<Vector3> relit = stackalloc Vector3[6];
        Span<Vector3> light = stackalloc Vector3[6];
        Span<Vector3> added = stackalloc Vector3[6];

        radiance[..6].CopyTo(destination: relit);
        for (var pass = 0; (pass < SdfMeshIrradianceProbe.CameraSampleClosurePasses); pass++) {
            for (var axis = 0; (axis < 6); axis++) { light[axis] = (radiance[axis] + sky[axis]); }
            added.Clear();
            for (var texel = 0; (texel < SdfMeshProbeDepth.HitCount); texel++) {
                if ((open[texel] == Vector3.Zero) || !SdfMeshProbeDepth.TryHit(backFace: out _, distance: out _, hits: hits, material: out _, normal: out var normal, texel: texel)) { continue; }

                var reflected = (open[texel] * Cube(cube: light, normal: normal, squared: (normal * normal)));

                for (var axis = 0; (axis < 6); axis++) {
                    added[axis] += (reflected * (weights[texel] * MathF.Max(x: 0f, y: Vector3.Dot(vector1: directions[texel], vector2: Axes[axis]))));
                }
            }
            for (var axis = 0; (axis < 6); axis++) { radiance[axis] = (relit[axis] + (added[axis] / AxisTotals[axis])); }
        }
    }

    /// <summary>Describes the volume: its tier's layout, the static scene, the lights it traces, the probes cached, the
    /// work done since it last settled, its budget and its lighting's age.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        lock (m_gate) {
            var buffers = (m_writeTables?.MeshIrradianceBufferBytes ?? default);

            var cached = 0;

            foreach (var level in m_levels) { cached += level.Cache.Count; }

            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-gi] spacing={SpacingText(spacing: m_layout.Spacing)} dims={m_layout.Dims.X}x{m_layout.Dims.Y}x{m_layout.Dims.Z} levels={string.Join(separator: ',', values: m_layout.Levels.Select(selector: static level => SpacingText(spacing: level.Spacing)))} measured-from={m_layout.Finest} first-light-owed={(OwesFirstLight ? "yes" : "no")} settling={DescribeSettling()} rays={SdfMeshProbeDepth.HitCount} bounce={(m_layout.Bounce ? "on" : "off")} triangles={(m_scene?.TriangleCount ?? 0)} lights={(m_lighting?.Points.Length ?? 0)} cached={cached} lighting-version={m_lightingVersion} local-version={m_localVersion} traced={m_traced} relit={m_relit} traced-rays={m_rays} {DescribeAge()} record-bytes={(SdfMeshIrradianceProbe.ProbeWords * sizeof(uint))} volume-bytes={m_volume.Bytes} table-device-bytes={buffers.DeviceLocal}");
        }
    }

    // The budget and the lighting's age, as key=value pairs; under the gate.
    private string DescribeAge() => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"budget-relight-per-frame={((m_budget.RelightPerFrame > 0) ? m_budget.RelightPerFrame.ToString(provider: CultureInfo.InvariantCulture) : "unbounded")} budget-trace-per-frame={((m_budget.TracePerFrame > 0) ? m_budget.TracePerFrame.ToString(provider: CultureInfo.InvariantCulture) : "unbounded")} age-target={((m_budget.Age > 0f) ? m_budget.Age.ToString(format: "0.##", provider: CultureInfo.InvariantCulture) : "none")} age-p50={m_ageMedian:0.##} age-max={m_ageMax:0.##} relit-per-second={m_cycleRate:0} {DescribeLevelAges()}");
    // Sample at the diagnostic read, rather than reporting the ages from the last work-list rebuild.
    private string DescribeLevelAges() {
        if (m_lighting is null || m_scene is null) { return ""; }
        var now = Time.GetTimestamp();
        var descriptions = new List<string>();
        var comparisons = new Dictionary<LightInputs, LightDelta>(ReferenceEqualityComparer.Instance);
        using var hold = m_blocks.Enter();

        for (var level = 0; (level < m_levels.Length); level++) {
            var low = m_levels[level].Low;
            var dims = m_layout.DimsOf(level: level);
            var ages = new List<float>();
            var visible = new List<float>();
            var outside = new List<float>();

            for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                    for (var x = low.X; (x < (low.X + dims.X)); x++) {
                        if (!m_levels[level].Cache.TryGetValue(key: (x, y, z), value: out var probe, hold: in hold)) { continue; }
                        var center = (new Vector3(x: (x + .5f), y: (y + .5f), z: (z + .5f)) * m_layout.SpacingOf(level: level));
                        var next = Math.Clamp(((probe.Lighting + 1) - m_versionBase), 0, (m_versionTimes.Count - 1));
                        var age = (ChangeOf(in probe, m_lighting, m_layout.SpacingOf(level), center, m_scene, comparisons) == InputChange.None ? 0 : (float)Time.GetElapsedTime(m_versionTimes[next], now).TotalSeconds);

                        ages.Add(item: age);

                        (((m_viewCameras.Length == 0) || InView(center, m_viewCameras[0], m_layout.Reach)) ? visible : outside).Add(item: age);
                    }
                }
            }
            ages.Sort();
            visible.Sort();
            outside.Sort();
            descriptions.Add(item: FormattableString.Invariant(formattable: $"level-{level}-interleave={SdfMeshProbeRelight.InterleaveOf(level: level)} level-{level}-age-bound={SdfMeshProbeRelight.AgeBoundOf(level: level, target: m_budget.Age):0.###} level-{level}-age-p50={Median(values: ages):0.###} level-{level}-age-max={Oldest(values: ages):0.###} level-{level}-first-view-count={visible.Count} level-{level}-first-view-age-p50={Median(values: visible):0.###} level-{level}-first-view-age-max={Oldest(values: visible):0.###} level-{level}-outside-count={outside.Count} level-{level}-outside-age-p50={Median(values: outside):0.###} level-{level}-outside-age-max={Oldest(values: outside):0.###}"));
        }
        return string.Join(separator: ' ', values: descriptions);

        static float Median(List<float> values) => ((values.Count == 0) ? 0 : OrderStatistics.Quantile(ascending: CollectionsMarshal.AsSpan(list: values), fraction: 0.5));
        static float Oldest(List<float> values) => ((values.Count == 0) ? 0 : values[^1]);
    }
    private void CaptureViewCameras(SdfFrame frame) {
        // Indexed: the frame's views are an interface list, whose enumerator boxes every frame.
        var views = frame.Views;
        var count = 0;

        for (var view = 0; (view < views.Count); view++) { if (!views[view].Elsewhere) { count++; } }
        if (m_viewCameras.Length != count) { m_viewCameras = new CameraSnapshot[count]; }
        var index = 0;

        for (var view = 0; (view < views.Count); view++) { if (!views[view].Elsewhere) { m_viewCameras[index++] = views[view].Camera; } }
    }
    private static bool InView(Vector3 center, CameraSnapshot camera, float far) => camera.MaySee(center, default, default, default, 0, far);
    private static bool InAnyView(Vector3 center, in Snapshot snapshot) {
        if (snapshot.ViewCameras.Length == 0) { return true; }
        foreach (var camera in snapshot.ViewCameras) { if (InView(center, camera, snapshot.Layout.Reach)) { return true; } }
        return false;
    }

    /// <summary>Describes the volume's cost sheet for <c>world.budget</c>: its probes per level, the bytes a probe takes in
    /// the table and in the CPU cache, the budget each frame grants, and the lighting age the current scene settles
    /// at.</summary>
    /// <returns>One clause, without brackets.</returns>
    public string DescribeBudget() {
        lock (m_gate) {
            if (!m_windowValid) { return "indirect volume not staged"; }

            var counts = string.Join(separator: '+', values: Enumerable.Range(count: SdfMeshIrradianceProbe.Levels, start: 0).Select(selector: level => m_layout.CountOf(level: level).ToString(provider: CultureInfo.InvariantCulture)));
            var cpuBytes = (((ProbeBlock.FloatsPerProbe + LightBlock.FloatsPerProbe) * sizeof(float)));

            return string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"indirect volume {counts} probe(s) over {SdfMeshIrradianceProbe.Levels} level(s) at {string.Join(separator: '/', values: m_layout.Levels.Select(selector: static level => SpacingText(spacing: level.Spacing)))} m, {(SdfMeshIrradianceProbe.ProbeWords * sizeof(uint))} byte(s) a probe in the table and {cpuBytes} in the CPU cache, {SdfMeshProbeDepth.HitCount} hit(s) a probe, bounce {(m_layout.Bounce ? "on" : "off")}; {DescribeAge()}");
        }
    }

    // A cell size as text: one number for a cube, else its three axes.
    private static string SpacingText(Vector3 spacing) => (((spacing.X == spacing.Y) && (spacing.Y == spacing.Z))
        ? spacing.X.ToString(format: "0.##", provider: CultureInfo.InvariantCulture)
        : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{spacing.X:0.##}x{spacing.Y:0.##}x{spacing.Z:0.##}"));
    // Whether a frame that does not converge lighting may build a scene from its static draws: once no build runs, the
    // installed scene's traces are paid (none owed, or as many traced since it was installed as the windows hold, so a
    // camera that keeps the windows moving never holds a real change back) or cannot be paid at all (m_sceneUnplaced: the
    // residency no longer places a mesh the scene names, so the trace would wait on the rebuild that waits on it), and
    // PendingSceneInterval has passed since the last build began. The trace's progress paces a rebuild only while the bake
    // still owes opaque static meshes (bakePending), so the probes finish measuring one scene before the next replaces it
    // while the draws keep arriving; once the bake owes none, the draws standing are the scene the probes are meant to
    // measure, and every probe still owed on the installed scene would measure geometry the next build replaces. A
    // lighting-converged frame builds its own scene (AwaitScene), and a build that faulted is always due, so RefreshScene
    // reports it.
    private bool ScenePendingDue(SdfFrame frame, bool bakePending) =>
        ((m_build is { IsFaulted: true }) ||
        (!frame.ConvergesLighting && (m_build is null or { IsCompleted: true }) && ((m_scene is null) || !m_traceOwed || m_sceneUnplaced || !bakePending || (m_tracedSinceScene >= m_layout.TotalCount)) &&
        ((m_sceneStartedAt == 0L) || (Time.GetElapsedTime(startingTimestamp: m_sceneStartedAt) >= PendingSceneInterval))));
    // The probes traced since the installed scene was (Install, Batch).
    private long m_tracedSinceScene;
    // Rebuilds the scene on the thread pool when the static draws changed, from the scene it replaces so only geometry new
    // to it builds; the old scene traces until the new one lands. While the bake still owes opaque static meshes, a frame
    // with none landed builds nothing: an empty scene would measure open sky where the walls have yet to land.
    // Returns the build to wait for before this frame can use its scene, or null when it is already installed. A build
    // that faulted is reported here, the one door every build passes, so whichever frame resolves next fails with it,
    // whatever its draws; until then Settling holds the frame, and the next frame builds again.
    private Task? RefreshScene(SdfFrame frame, bool bakePending = false) {
        if (m_build is { IsFaulted: true } faulted) {
            (m_build, m_buildingKey) = (null, null);
            throw new InvalidOperationException(message: "The irradiance volume's scene build failed.", innerException: faulted.Exception);
        }

        var (key, draws) = StaticKey(frame: frame);

        // Even a frame matching the installed scene waits for an older build: that build could otherwise replace
        // the scene while this frame traces it. The lockstep caller resolves this frame again after it lands.
        if (m_build is { IsCompleted: false }) { return m_build; }
        if (key == m_sceneKey) { m_sceneRefusedAt = null; return null; }
        if (WaitingForMemory(m_sceneRefusedAt)) { return null; }
        if (key == m_buildingKey) { return m_build; }
        if (bakePending && (draws == 0)) { return null; }

        return StartSceneBuild(draws: draws, frame: frame, key: key);
    }
    // An order-independent multiset digest, updated only for cursor-named rows; packing leaves the scene standing.
    private (MultisetDigest Key, int Draws) StaticKey(SdfFrame frame) {
        var draws = frame.DrawRows;
        var whole = m_keyCursor.Collect(draws.Changes, draws.Serial, draws.Roster, draws.Count,
            draws.Revision, m_keyRuns);

        if (whole) {
            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.SceneWhole);
            m_keyValue = MultisetDigest.Empty; m_keyStatic = 0;
            Array.Clear(array: m_drawStatic); Array.Clear(array: m_drawKeys);
            m_staticRows.Clear();
        }
        if (m_drawKeys.Length < draws.Count) { var size = ArrayCapacity.Grown(m_drawKeys.Length, draws.Count); Array.Resize(array: ref m_drawKeys, newSize: size); Array.Resize(array: ref m_drawStatic, newSize: size); }
        for (var row = draws.Count; (row < m_keyExtent); row++) {
            if (m_drawStatic[row]) { m_keyValue = m_keyValue.Remove(element: m_drawKeys[row]); m_keyStatic--; }
            m_drawStatic[row] = false; m_drawKeys[row] = 0;
            m_staticRows.Remove(item: row);
        }
        for (var run = 0; (run < m_keyRuns.Count); run++) {
            for (var row = m_keyRuns.Start(index: run); (row < (m_keyRuns.Start(index: run) + m_keyRuns.Length(index: run))); row++) {
                if (m_drawStatic[row]) { m_keyValue = m_keyValue.Remove(element: m_drawKeys[row]); m_keyStatic--; }
                var draw = draws[row];
                var isStatic = draw.InStaticScene;

                m_drawStatic[row] = isStatic;
                if (isStatic) { m_staticRows.Add(item: row); } else { m_staticRows.Remove(item: row); }
                m_drawKeys[row] = (isStatic ? DrawKey(draw: in draw) : 0);
                if (isStatic) { m_keyValue = m_keyValue.Add(element: m_drawKeys[row]); m_keyStatic++; }
            }
        }
        m_keyExtent = draws.Count;
        if (SdfRowChecks.SkipSceneRequested) { m_skipKeyThrough = ((draws.Serial + SdfMovedTransforms.History) + 2); SdfRowChecks.SkipSceneRequested = false; }
        if (draws.Serial <= m_skipKeyThrough) {
            SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.SceneSkipped);
            // Leave this consumer's cursor at its previous endpoint until its history expires.
        } else { m_keyCursor.Commit(); }
        return (m_keyValue, m_keyStatic);
    }
    // A static row's element of the scene key: what the scene holds for it (TraceDraws), so a row whose drawn mesh moves
    // to another tier while it traces its group's floor keeps the key. The mesh counts by reference, as the scene holds
    // it, the transform and material by their bits. Zero, which the digest's mix leaves out, stands for a row that
    // adds nothing to the scene.
    private static ulong DrawKey(in SdfMeshDraw draw) => ((draw.Trace is { } trace)
        ? ((draw.Piece == 0) ? DrawElement(material: draw.Material, mesh: trace, objectToWorld: draw.FixedPlacement) : 0UL)
        : DrawElement(material: draw.Material, mesh: draw.Mesh, objectToWorld: draw.FixedPlacement));
    private static ulong DrawElement(SdfMesh mesh, in Matrix4x4 objectToWorld, int material) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: ReferenceSerial.Of(value: mesh));
        hash.AddBits(value: in objectToWorld);
        hash.Add(value: unchecked((uint)material));
        return hash.Value;
    }
    // Appends what the trace scene holds for one static row: its drawn mesh, or where the row's placement traces its
    // group's floor (SdfMeshDraw.Trace), every piece of that floor once, at the placement's first piece.
    private static int TraceDraws(in SdfMeshDraw draw, ref SdfMeshDraw[] into, int count) {
        if (draw.Trace is not { } trace) {
            if (count == into.Length) { into = RentedArrays.Grow(array: into, keep: count, minimumLength: Math.Max(val1: 16, val2: (2 * count))); }
            into[count++] = draw;
            return count;
        }
        if (draw.Piece != 0) { return count; }
        for (var piece = 0; (piece < trace.PieceCount); piece++) {
            var traced = (draw with { Mesh = trace.Piece(index: piece), Piece = 0, Trace = null });

            if (!traced.IsStatic) { continue; }
            if (count == into.Length) { into = RentedArrays.Grow(array: into, keep: count, minimumLength: Math.Max(val1: 16, val2: (2 * count))); }
            into[count++] = traced;
        }
        return count;
    }
    // Apart from RefreshScene, since the build's closure is made on entry to the method holding it, and nearly every frame
    // matches the installed scene.
    private Task StartSceneBuild(SdfFrame frame, MultisetDigest key, int draws) {
        lock (m_gate) { if (m_disposed) { return Task.CompletedTask; } }
        if (!m_trimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested += TrimRetained; m_trimListening = true; }

        // What the scene holds for the frame's static draws, its whole input, into the spare array (indexed: an interface
        // list's enumerator is an allocation). Its tail past them is cleared, so the pooled array keeps no earlier draw's
        // mesh.
        var meshDraws = frame.DrawRows;
        var snapshot = ((m_snapshotDraws.Length < draws) ? RentedArrays.Grow(array: m_snapshotDraws, keep: 0, minimumLength: draws) : m_snapshotDraws);
        var count = 0;
        var floors = 0;

        foreach (var index in m_staticRows) {
            var draw = meshDraws[index];

            if (!draw.InStaticScene) { continue; }
            if ((draw.Trace is not null) && (draw.Piece == 0)) { floors++; }
            count = TraceDraws(count: count, draw: in draw, into: ref snapshot);
        }
        snapshot.AsSpan(start: count).Clear();
        m_snapshotDraws = snapshot;

        var input = new ArraySegment<SdfMeshDraw>(array: snapshot, count: count, offset: 0);

        m_snapshotClaims.Update(draws: input);

        var previous = m_scene?.Acquire();

        var (previousDraws, previousCount) = (m_diagnosticDraws, m_diagnosticCount);
        var hadScene = (m_scene is not null);
        var since = ((m_sceneStartedAt == 0L) ? 0d : Time.GetElapsedTime(startingTimestamp: m_sceneStartedAt).TotalSeconds);

        m_buildingKey = key;
        m_sceneStartedAt = Time.GetTimestamp();
        m_build = Task.Run(function: async () => {
            var clock = Stopwatch.StartNew();
            SdfMeshIrradianceScene? scene;
            ChangeTree? changed;

            using (previous) {
                scene = TryBuildScene(input, previous, Counters);
                if (scene is null) {
                    lock (m_gate) {
                        m_sceneRefusedAt = Time.GetTimestamp();
                        m_buildingKey = null;
                        m_snapshotClaims.Update(draws: Array.Empty<SdfMeshDraw>());
                        Array.Clear(snapshot);
                    }
                    return;
                }
                changed = (hadScene ? ChangedBoxes(after: snapshot.AsSpan(length: count, start: 0), before: previousDraws.AsSpan(length: previousCount, start: 0)) : null);
            }
            Counters.Count(kind: Scenes);
            Counters.Add(amount: scene.GeometriesBuilt, kind: GeometriesBuilt);
            Counters.Add(amount: scene.GeometriesReused, kind: GeometriesReused);
            Counters.Add(amount: scene.TrianglesBuilt, kind: TrianglesBuilt);
            Counters.Add(amount: scene.DrawCount, kind: StaticDraws);
            Counters.Add(amount: scene.EntryCount, kind: Entries);
            Counters.Raise(kind: PeakEntries, value: scene.EntryCount);

            var install = new SceneInstall(Changed: changed, Count: count, Draws: snapshot, Key: key, Scene: scene);
            var held = false;

            lock (m_gate) {
                if (m_disposed) { scene.Dispose(); return; }
                // A trace unit running on the installed scene lands first, then the scene changes (EndUnit).
                if (m_units > 0) {
                    m_pendingInstall = install;
                    held = true;
                } else {
                    Install(install: install);
                }
            }
            var built = clock.Elapsed.TotalSeconds;

            if (held) { await install.Done.Task.ConfigureAwait(continueOnCapturedContext: false); }
            Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-gi] scene draws={count} floors={floors} triangles={scene.TriangleCount} geometries={scene.GeometryCount} built={scene.GeometriesBuilt} built-triangles={scene.TrianglesBuilt} reused={scene.GeometriesReused} retained-triangles={scene.TrianglesRetained} prefix-words={scene.PrefixWords} changed={((changed is null) ? "all" : changed.Boxes.ToString(provider: CultureInfo.InvariantCulture))} change-nodes={(changed?.Nodes.Length ?? 0)} since={since:0.0} seconds={built:0.000} held={(clock.Elapsed.TotalSeconds - built):0.000}"));
        });
        return m_build;
    }
    private bool WaitingForMemory(long? refusedAt) => refusedAt is { } at && Time.GetElapsedTime(at) < PendingSceneInterval;

    // Parallel hierarchy workers can report several refusals together. Other build faults still reach the caller.
    private SdfMeshIrradianceScene? TryBuildScene(IReadOnlyList<SdfMeshDraw> draws, SdfMeshIrradianceScene? previous, WorkCounterSet? counters = null) {
        try { return SdfMeshIrradianceScene.Build(draws, previous, counters, traceError: TraceError, allocator: m_allocator); }
        catch (Exception exception) when (HostMemoryRefusedException.Of(exception: exception) is not null) { return null; }
    }
    // A built scene waiting to replace the installed one: its input, its key and the change it makes; Done completes
    // once it is installed, or dropped by a disposed volume.
    private sealed record SceneInstall(SdfMeshIrradianceScene Scene, MultisetDigest Key, SdfMeshDraw[] Draws, int Count, ChangeTree? Changed) {
        public TaskCompletionSource Done { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    }
    // The trace units in flight (BeginUnit): a listing or a batch the worker runs on the snapshot it took. A scene built
    // while one runs waits in m_pendingInstall and replaces the installed scene once the last ends, so a rebuild never
    // drops a batch traced or relit against the scene it replaces, nor cancels its queued GPU jobs: the batch lands with
    // the geometry it measured, and the next listing decides from the change what traces again. Under the gate.
    private int m_units;
    private SceneInstall? m_pendingInstall;

    private void BeginUnit() {
        lock (m_gate) { m_units++; }
    }
    private void EndUnit() {
        lock (m_gate) {
            m_units--;
            if ((m_units > 0) || (m_pendingInstall is not { } install)) { return; }
            m_pendingInstall = null;
            if (m_disposed) { install.Scene.Dispose(); } else { Install(install: install); }
            install.Done.TrySetResult();
        }
    }
    // Installs a built scene as the one the probes trace: the one place the trace scene changes. Under the gate.
    private void Install(SceneInstall install) {
        var oldScene = m_scene;

        m_scene = install.Scene;
        oldScene?.Dispose();
        // The replaced draws' array becomes the next snapshot's.
        (m_snapshotDraws, m_diagnosticDraws, m_diagnosticCount) = (m_diagnosticDraws, install.Draws, install.Count);
        m_sceneKey = install.Key;
        m_sceneRefusedAt = null;
        m_buildingKey = null;
        m_tracedSinceScene = 0L;
        m_sceneUnplaced = false;
        AdvanceGeometry(changed: install.Changed);
    }

    // Captures the lighting the probes are relit under, and owes a relight when it changes: the directional lights and the
    // static point and spot lights, whole, so a fixture switched off relights the bounce it cast; the sky the bounce
    // reflects; the gains, the sources, the sweeps and what the scene's materials reflect and emit. Nonconverged
    // captures are rate-limited because a capture renders the sky. An authored grid snaps the capture inputs and
    // radiance independently of history. A sky capture is kept or replaced by the sky texels the relight reads
    // (Lighting.SkyMatches), exactly under a converged frame or a grid.
    private readonly SdfLights m_captureLights = new();

    private void RefreshLighting(SdfFrame frame, Layout layout) {
        var grid = frame.IndirectLightingGrid;

        grid.Validate();
        var captureLights = frame.Lights;

        if (grid.Enabled) {
            m_captureLights.CopyFrom(source: frame.Lights);
            for (var index = 0; (index < frame.Lights.Count); index++) { m_captureLights.Set(index: index, light: grid.Snap(light: frame.Lights[index])); }
            captureLights = m_captureLights;
        }
        // Both keys are 64-bit digests of every input bit they name: equal keys stand for unchanged lighting.
        var local = Fnv1aHash.Create();
        var gains = frame.IndirectGains;

        local.AddBits(value: gains.Lights); local.AddBits(value: gains.Emission); local.AddBits(value: gains.Screens);
        local.AddBits(value: gains.Sky); local.AddBits(value: gains.Feedback);
        local.Add(value: (uint)frame.IndirectSources);
        local.Add(value: layout.Bounce ? 1U : 0U);
        local.Add(value: MaterialsKey(program: frame.Program));

        var sameSky = true;
        var skyBlock = default(SdfSkyBlock);
        var records = captureLights.Records;
        var roles = MultisetDigest.Empty;

        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = records[index];

            if (light.Kind == SdfLightKind.Directional && light.Weight > 0f) {
                roles = roles.Add(((ulong)index << 1) | (light.CastsShadow ? 1UL : 0UL));
            }
            if (light.IsTraced && (light.Kind != SdfLightKind.Directional)) { AddLight(hash: ref local, index: index, light: in light); }
        }

        // Local cube reuse also copies visibility bits. A directional leaving its slot must clear its old bit.
        local.Add(value: roles.Value);
        var localKey = local.Value;
        var whole = Fnv1aHash.Create();

        whole.Add(value: localKey);

        whole.AddBits(value: grid.DirectionDegrees); whole.AddBits(value: grid.Radiance);
        whole.Add(value: (frame.ConvergesLighting ? 1U : 0U));
        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = captureLights[index];

            if (light.Kind == SdfLightKind.Directional) { AddLight(hash: ref whole, index: index, light: in light); }
        }

        var key = whole.Value;

        if ((frame.IndirectSources & SdfIndirectSources.Sky) != 0) {
            frame.Sky.Pack(captureLights, frame.FarDistance, m_skyDetails, out skyBlock, m_skyLayers);
            sameSky = SdfSkyEnvironment.SameMap(block: skyBlock, layers: m_skyLayers, otherBlock: m_lightingSky, otherLayers: m_lightingSkyLayers);
        }
        if ((key == m_lightingKey) && sameSky && (m_lighting is not null)) {
            Interlocked.Increment(location: ref m_lightingMatches);
            return;
        }
        if (!frame.ConvergesLighting && (m_lighting is not null) && (Time.GetElapsedTime(startingTimestamp: m_lightingCheckedAt) < LightingInterval)) { return; }

        var skyOnly = ((key == m_lightingKey) && (m_lighting is not null));

        if ((localKey != m_localKey) || (m_lighting is null)) {
            m_localKey = localKey;
            m_localVersion++;
        }

        var captured = Lighting.Capture(roles: roles, captureLights: captureLights, frame: frame, localVersion: m_localVersion, previous: m_lighting, scratch: m_captureScratch, capturedAt: Time.GetTimestamp(), version: (m_lightingVersion + 1));

        if ((frame.IndirectSources & SdfIndirectSources.Sky) != 0) { Counters.Count(kind: SkyCaptures); }
        // Exposure reads the current capture's bound even when its irradiance stays in the same grid cell.
        m_skyPeak = captured.SkyPeak;

        m_lightingCheckedAt = Time.GetTimestamp();
        m_lightingKey = key;
        m_lightingSky = skyBlock;
        m_skyLayers.CopyTo(array: m_lightingSkyLayers, index: 0);
        Interlocked.Increment(location: ref m_lightingMatches);
        if (skyOnly && captured.SkyMatches(other: m_lighting!, exact: (frame.ConvergesLighting || grid.Enabled))) { return; }
        Volatile.Write(location: ref m_lightingVersion, value: (m_lightingVersion + 1));
        m_lighting = captured;
        m_versionTimes.Add(item: captured.CapturedAt);
        if (m_versionTimes.Count > MaxVersionTimes) {
            m_versionTimes.RemoveRange(count: (MaxVersionTimes / 2), index: 0);
            m_versionBase += (MaxVersionTimes / 2);
        }
        Owe();
    }
    private long m_lightingMatches;
    /// <summary>Gets how many staged frames the volume's lighting answered: each frame whose lighting it had captured
    /// already, or captured then. A frame whose changed lighting waits for the capture interval is not counted, so a
    /// count past one read at a frame that carried an edit says the volume holds the lighting of that edit or a later one.
    /// Safe to read from any thread.</summary>
    public long LightingMatches => Interlocked.Read(location: ref m_lightingMatches);
    /// <summary>Gets how many lightings the volume has captured and relights under, counting from one. Safe to read from any
    /// thread.</summary>
    public int LightingCaptures => Volatile.Read(location: ref m_lightingVersion);
    // The part of a program's materials the relight reads (Shade: what each reflects and emits), hashed once per program
    // and scene: the materials the static scene's triangles carry (SdfMeshIrradianceScene.Materials), by id. A program
    // rebuilt with one of them changed in place (a bound color's move, a placement showing another creation over the same
    // meshes) leaves the static draws as they were, so only this key relights it; a body's palette coming or going, which
    // no probe ray meets, leaves it alone.
    private ulong MaterialsKey(SdfProgram program) {
        if (ReferenceEquals(objA: program.Appearance, objB: m_materialsAppearance) && ReferenceEquals(objA: m_scene, objB: m_materialsScene)) { return m_materialsKey; }

        var hash = Fnv1aHash.Create();
        var materials = program.Appearance.Materials;

        foreach (var id in (m_scene?.Materials ?? [])) {
            hash.Add(value: unchecked((uint)id));
            if ((id < 0) || (id >= materials.Count)) {
                hash.Add(value: 0U);
                continue;
            }

            var material = materials[id];

            hash.Add(value: 1U);
            hash.AddBits(value: material.Albedo);
            hash.AddBits(value: material.Emissive);
            hash.AddBits(value: material.Metal);
            hash.AddBits(value: material.Wrap);
            hash.Add(value: (material.Bleed.HasValue ? 1U : 0U));
            hash.AddBits(value: material.Bleed.GetValueOrDefault());
            hash.Add(value: (material.EmissiveLane.HasValue ? 1U : 0U));
            hash.Add(value: unchecked((uint)material.EmissiveLane.GetValueOrDefault()));
        }
        m_materialsAppearance = program.Appearance;
        m_materialsScene = m_scene;
        m_materialsKey = hash.Value;

        return m_materialsKey;
    }
    // Folds a light whole, with its index: its explicit 64-byte record has no padding, so its bytes are its fields.
    private static void AddLight(ref Fnv1aHash hash, int index, in SdfLight light) {
        hash.Add(value: unchecked((uint)index));
        hash.Add(values: MemoryMarshal.AsBytes(span: new ReadOnlySpan<SdfLight>(reference: in light)));
    }
    // Whether a cell lies in a window's middle: an eighth of the window, at least one cell, short of each face.
    private static bool InMiddle((int X, int Y, int Z) cell, (int X, int Y, int Z) dims, (int X, int Y, int Z) low) =>
        (Middle(cell: cell.X, dims: dims.X, low: low.X) && Middle(cell: cell.Y, dims: dims.Y, low: low.Y) && Middle(cell: cell.Z, dims: dims.Z, low: low.Z));
    private static bool Middle(int cell, int low, int dims) {
        var margin = MarginOf(dims: dims);

        return ((cell >= (low + margin)) && (cell < ((low + dims) - margin)));
    }
    // The cells along one axis of a window's band: a camera inside the window short of it keeps the window where it stands
    // (InMiddle), and a level's cache keeps the probes this far past its window (Resident).
    private static int MarginOf(int dims) => Math.Max(val1: 1, val2: (dims / 8));
    // The cells a level's cache may hold about its window: the window grown by its band on every side, so a camera that
    // moves the window back across that band finds its probes still measured. Every probe outside it is forgotten once the
    // cache holds more probes than the box has cells, which bounds the cache by the box whatever the camera has visited.
    private static ((int X, int Y, int Z) Low, (int X, int Y, int Z) High) Resident((int X, int Y, int Z) low, (int X, int Y, int Z) dims) {
        var margin = (MarginOf(dims: dims.X), MarginOf(dims: dims.Y), MarginOf(dims: dims.Z));

        return (((low.X - margin.Item1), (low.Y - margin.Item2), (low.Z - margin.Item3)), (((low.X + dims.X) + margin.Item1), ((low.Y + dims.Y) + margin.Item2), ((low.Z + dims.Z) + margin.Item3)));
    }
    private bool InWindow(int level, (int X, int Y, int Z) cell) {
        var low = m_levels[level].Low;
        var dims = m_layout.DimsOf(level: level);

        return ((cell.X >= low.X) && (cell.X < (low.X + dims.X)) && (cell.Y >= low.Y) && (cell.Y < (low.Y + dims.Y)) &&
            (cell.Z >= low.Z) && (cell.Z < (low.Z + dims.Z)));
    }
    // Each level's window's first cell, as the worker reads them outside the gate.
    // The array is never written after it is returned, so a worker may hold it while a later call replaces it; a window
    // that has not moved answers the array it answered last.
    private (int X, int Y, int Z)[] Lows() {
        var held = m_lows;
        var same = (held.Length == m_levels.Length);

        for (var level = 0; (same && (level < m_levels.Length)); level++) { same = (held[level] == m_levels[level].Low); }
        if (same) { return held; }

        var lows = new (int X, int Y, int Z)[m_levels.Length];

        for (var level = 0; (level < m_levels.Length); level++) { lows[level] = m_levels[level].Low; }
        return (m_lows = lows);
    }
    private (int X, int Y, int Z)[] m_lows = [];
    // Owes the refill of the windows the volume just moved. It runs on the thread
    // pool (Refill), since a window that jumps far re-stores every slot; a lighting-converged frame waits for it.
    private void OweRefill() {
        m_windowVersion++;
        m_refillOwed = true;
        foreach (var level in m_levels) { level.Unwritten = (m_layout.Measures(level: level.Index) ? m_layout.CountOf(level: level.Index) : 0); }
        if (!m_refillRunning) {
            m_refillRunning = true;
            m_refill = Task.Run(action: Refill);
        }
    }
    // Writes every cached probe of each window whose slot holds another cell's: the cells are listed under the gate, their
    // slots composed outside it, and written under it again unless a window moved meanwhile, when the next round lists
    // the new windows. A probe that lands in between stores itself and is left as it stands.
    private void Refill() {
        while (true) {
            int version;

            lock (m_gate) {
                if (!m_refillOwed) {
                    m_refillRunning = false;
                    return;
                }

                version = m_windowVersion;
                m_refillCells.Clear();
                foreach (var level in m_levels) {
                    if (!m_layout.Measures(level: level.Index)) {
                        level.Unwritten = 0;
                        continue;
                    }

                    var low = level.Low;
                    var dims = m_layout.DimsOf(level: level.Index);
                    var unwritten = m_layout.CountOf(level: level.Index);

                    for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                        for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                            for (var x = low.X; (x < (low.X + dims.X)); x++) {
                                if (Holds(cell: (x, y, z), level: level.Index)) {
                                    unwritten--;
                                    continue;
                                }
                                // GPU records are restored by a selected relight and its guarded device commit.
                                if ((GpuTrace is null) && level.Cache.ContainsKey(key: (x, y, z))) { m_refillCells.Add(item: (level.Index, (x, y, z))); }
                            }
                        }
                    }

                    level.Unwritten = unwritten;
                }
            }

            var cells = m_refillCells.Count;
            // Native, for this round alone: a window that jumps far composes every slot, and the words are written before
            // the round ends, so none of a volume's size stays held between refills.
            using var words = OwnedBuffer<uint>.OfLength(allocator: m_allocator, clear: false, length: (cells * SdfMeshIrradianceProbe.ProbeWords));
            var composed = ComposeCells(cells: m_refillCells, words: words);

            lock (m_gate) {
                if (version != m_windowVersion) { continue; }

                for (var index = 0; (index < composed); index++) {
                    var (level, cell) = m_refillCells[index];

                    if (Holds(cell: cell, level: level)) { continue; }
                    words.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords))
                        .CopyTo(destination: SlotWords(cell: cell, level: level));
                    m_levels[level].Unwritten--;
                }

                m_refillOwed = false;
                m_refillRunning = false;

                return;
            }
        }
    }
    // Composes each listed cell's slot words from the cache, outside the gate, into consecutive rows of the words, and
    // keeps the cells that composed at the front of the list in the same order: the count of them. A refill and a landed
    // batch's store both write those rows under the gate once a window has not moved meanwhile.
    private int ComposeCells(List<(int Level, (int X, int Y, int Z) Cell)> cells, OwnedBuffer<uint> words) {
        var composed = 0;
        using var hold = m_blocks.Enter();

        for (var index = 0; (index < cells.Count); index++) {
            var entry = cells[index];

            if (Compose(cell: entry.Cell, level: entry.Level, words: words.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (composed * SdfMeshIrradianceProbe.ProbeWords)), hold: in hold)) {
                cells[composed++] = entry;
            }
        }
        return composed;
    }
    // The slot a level's cell takes in the volume: its level's first slot plus its slot in the level's window.
    private int VolumeSlot(int level, (int X, int Y, int Z) cell) =>
        (m_layout.OffsetOf(level: level) + SlotOf(dims: m_layout.DimsOf(level: level), x: cell.X, y: cell.Y, z: cell.Z));
    // Whether a level's cell's slot holds that cell's probe.
    private bool Holds(int level, (int X, int Y, int Z) cell) =>
        Holds(m_volume.Words[(VolumeSlot(cell: cell, level: level) * SdfMeshIrradianceProbe.ProbeWords)..], cell);

    private static bool Holds(ReadOnlySpan<uint> words, (int X, int Y, int Z) cell) =>
        (words[0] & SdfMeshIrradianceProbe.WrittenFlag) != 0 &&
        words[SdfMeshIrradianceProbe.CellWord] == (uint)cell.X &&
        words[SdfMeshIrradianceProbe.CellWord + 1] == (uint)cell.Y &&
        words[SdfMeshIrradianceProbe.CellWord + 2] == (uint)cell.Z;
    // A level's cell's slot words in the volume, marked changed for the level's next publication.
    private Span<uint> SlotWords(int level, (int X, int Y, int Z) cell, bool gpuWritten = false) {
        var slot = VolumeSlot(cell: cell, level: level);

        if ((GpuTrace is not null) && !gpuWritten) { _ = m_gpuDirty.Add(index: slot); }

        _ = m_levels[level].Changed.Add(index: slot);
        return m_volume.Words.Slice(length: SdfMeshIrradianceProbe.ProbeWords, start: (slot * SdfMeshIrradianceProbe.ProbeWords));
    }
    // Composes a level's cached cell's slot words from its own sky shares, relit cubes, surface map and light visibility;
    // false, writing nothing, when the cache holds no probe of it. It reads the cache alone, so it runs outside the gate.
    private bool Compose(int level, (int X, int Y, int Z) cell, Span<uint> words, in ReaderEpochs<Reclaimable>.Hold hold) {
        if (!m_levels[level].Cache.TryGetValue(key: cell, value: out var probe, hold: in hold)) { return false; }
        Compose(cell: cell, probe: in probe, words: words);
        return true;
    }
    private static void Compose(in ProbeData probe, (int X, int Y, int Z) cell, Span<uint> words) {

        var sky = probe.Sky;
        var radiance = probe.Radiance;
        var skyLight = probe.SkyLight;

        words[0] = SdfMeshIrradianceProbe.WrittenFlag | (probe.Invalid ? SdfMeshIrradianceProbe.InvalidFlag : 0u);
        words[SdfMeshIrradianceProbe.CellWord] = (uint)cell.X;
        words[SdfMeshIrradianceProbe.CellWord + 1] = (uint)cell.Y;
        words[SdfMeshIrradianceProbe.CellWord + 2] = (uint)cell.Z;
        for (var pair = 0; (pair < 3); pair++) {
            words[(SdfMeshIrradianceProbe.SkyWord + pair)] = VectorFunctions.PackHalf2x16(low: sky[(2 * pair)], high: sky[((2 * pair) + 1)]);
        }
        for (var pair = 0; (pair < 9); pair++) {
            words[(SdfMeshIrradianceProbe.RadianceWord + pair)] = VectorFunctions.PackHalf2x16(low: Channel(cube: radiance, index: (2 * pair)), high: Channel(cube: radiance, index: ((2 * pair) + 1)));
            words[(SdfMeshIrradianceProbe.SkyLightWord + pair)] = VectorFunctions.PackHalf2x16(low: Channel(cube: skyLight, index: (2 * pair)), high: Channel(cube: skyLight, index: ((2 * pair) + 1)));
        }
        probe.Lights.CopyTo(destination: words.Slice(length: SdfMeshIrradianceProbe.LightWords, start: SdfMeshIrradianceProbe.LightsWord));
        // KEEP IN SYNC with sdfMeshProbeOffset.
        words[SdfMeshIrradianceProbe.OffsetWord] = VectorFunctions.PackHalf2x16(high: probe.Offset.Y, low: probe.Offset.X);
        words[(SdfMeshIrradianceProbe.OffsetWord + 1)] = VectorFunctions.PackHalf2x16(high: probe.Inside, low: probe.Offset.Z);
        probe.Depth.CopyTo(destination: words[SdfMeshIrradianceProbe.DepthWord..]);

    }
    private static float Channel(ReadOnlySpan<Vector3> cube, int index) {
        var value = cube[(index / 3)];

        return ((index % 3) switch { 0 => value.X, 1 => value.Y, _ => value.Z });
    }

    /// <summary>Returns the slot a cell's probe takes in a volume: its coordinates modulo the volume's size. KEEP IN SYNC
    /// with <c>sdfMeshIrradianceSlot</c>.</summary>
    /// <param name="x">The cell's first coordinate.</param>
    /// <param name="y">The cell's second coordinate.</param>
    /// <param name="z">The cell's third coordinate.</param>
    /// <param name="dims">The volume's probes along each axis.</param>
    /// <returns>The slot.</returns>
    public static int SlotOf(int x, int y, int z, (int X, int Y, int Z) dims) =>
        (x.FloorModulo(modulus: dims.X) + (dims.X * (y.FloorModulo(modulus: dims.Y) + (dims.Y * z.FloorModulo(modulus: dims.Z)))));

    // The state a worker reads under the gate before it lists or processes probes.
    private readonly record struct Snapshot(SdfMeshIrradianceScene Scene, Lighting Lighting, Layout Layout, (int X, int Y, int Z)[] Lows, Vector3 Camera, int Geometry, int WindowVersion,
        (int Geometry, ChangeTree? Tree)[] Changes, long[] VersionTimes, int VersionBase, int Sweep, float AgeTarget, CameraSnapshot[] ViewCameras);

    private Snapshot? TakeSnapshot() {
        lock (m_gate) {
            if (m_disposed || (m_scene is null) || (m_lighting is null)) { return null; }
            return new Snapshot(Camera: m_camera, Changes: [.. m_changes], Geometry: m_geometry, Layout: m_layout, Lighting: m_lighting, Lows: Lows(), Scene: m_scene.Acquire(),
                Sweep: m_sweep, VersionBase: m_versionBase, VersionTimes: [.. m_versionTimes], WindowVersion: m_windowVersion, AgeTarget: m_budget.Age, ViewCameras: [.. m_viewCameras]);
        }
    }
    // Whether the windows, the layout and the geometry a worker listed its probes for still stand.
    private bool Stands(in Snapshot snapshot) =>
        ((snapshot.Geometry == m_geometry) && (snapshot.WindowVersion == m_windowVersion) && (snapshot.Layout == m_layout));
    // Whether everything a drain's light is a function of (DrainKey) still stands as the drain snapshot it, on a volume
    // not disposed: the one test a drain's every batch and its record of what it drained pass.
    private bool DrainStands(in Snapshot snapshot) => (!m_disposed && Stands(snapshot: in snapshot) && (snapshot.Lighting.Version == m_lightingVersion));
    // The background worker. Stable lighting takes whole-window bounce sweeps; changing lighting interleaves coarse
    // phases, with overdue probes forced. Its list holds trace demand, then the relights in order of overdue, lighting
    // stale, relative age, view/change weight and spatial bands, each probe reading the volume about its hits as it
    // stands. The worker shares its time evenly between the traces and the relights that are overdue or lighting-stale
    // (Next), so a lighting change is relit while a window's first fill still traces, and takes the other relights once
    // the traces are done; another sweep starts while any probe of it moved past the convergence bound or was relit under
    // a lighting since replaced, so a change anywhere in what a probe reads wakes it through the next sweep, and the
    // iteration stops only when a whole sweep under unchanged inputs moved nothing (or after MaxSweeps). The worker works
    // through its list a batch at a time while the frames' budget grants it, taking the latest lighting for each batch,
    // and lists again once the lighting changes; it returns once the budget is spent (a probe whose trace the budget
    // cannot fund waits for the next grant), when a lighting-converged frame asks it to yield, or once nothing is left to
    // do.
    private void Work() {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = BackgroundWorkers };

        while (true) {
            lock (m_gate) {
                if (m_yield) { return; }
            }
            if (TakeSnapshot() is not { } snapshot) { return; }
            using var sceneBorrow = snapshot.Scene;
            var candidates = m_candidates;

            // A list the budget interrupted resumes where it stopped while its geometry, windows, sweep and lighting
            // stand: listing and ordering a window's probes is itself work a frame's grant should not repeat, but a list
            // made under replaced lighting holds none of the probes it left stale.
            if (!m_listed || (m_listGeometry != snapshot.Geometry) || (m_listWindow != snapshot.WindowVersion) || (m_listLayout != snapshot.Layout) ||
                (m_listSweep != snapshot.Sweep) || (m_listLighting != snapshot.Lighting.Version)) {
                var now = Time.GetTimestamp();
                bool stale;

                BeginUnit();
                try { stale = List(ages: m_ages, all: false, candidates: candidates, snapshot: snapshot); } finally { EndUnit(); }

                lock (m_gate) {
                    if (!Stands(snapshot: in snapshot) || (snapshot.Sweep != m_sweep)) { continue; }
                    Measure(now: now);
                    m_nextLightCheck = m_listingLightCheck;
                    if (candidates.Count == 0) {
                        m_listed = false;
                        NoteSeenRelights(cursor: 0, end: 0);
                        if (stale || m_sweepMoved || (snapshot.Lighting.Version != m_lightingVersion)) {
                            // A partial interleaved sweep cannot certify bounce convergence or spend a full-sweep limit.
                            if (m_interleaving || (m_sweepsSince < MaxSweeps)) {
                                m_sweep++;
                                if (!m_interleaving) { m_sweepsSince++; }
                                m_sweepMoved = false;
                                continue;
                            }
                            if (!m_unconvergedReported) {
                                m_unconvergedReported = true;
                                Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-gi] unconverged sweeps={m_sweepsSince}: the bounce still moved past its bound; the volume stops until its inputs change"));
                            }
                        }
                        m_owed = false;
                        m_traceOwed = false;
                        Report(snapshot: in snapshot);
                        return;
                    }
                    if (!candidates[0].Trace) { m_traceOwed = false; }
                }
                m_listed = true;
                m_listGeometry = snapshot.Geometry;
                m_listWindow = snapshot.WindowVersion;
                m_listLayout = snapshot.Layout;
                m_listSweep = snapshot.Sweep;
                m_listLighting = snapshot.Lighting.Version;
                m_listTraces = 0;
                while ((m_listTraces < candidates.Count) && candidates[m_listTraces].Trace) { m_listTraces++; }
                m_listUrgent = m_listTraces;
                while ((m_listUrgent < candidates.Count) && (candidates[m_listUrgent].Overdue || candidates[m_listUrgent].Stale)) { m_listUrgent++; }
                var seen = m_listTraces;
                while ((seen < candidates.Count) && candidates[seen].Seen) { seen++; }
                m_listTraceCursor = 0;
                m_listRelightCursor = m_listTraces;
                NoteSeenRelights(cursor: m_listRelightCursor, end: seen);
                m_listTraceTicks = 0L;
                m_listRelightTicks = 0L;
            }

            while (true) {
                Lighting lighting;
                int index;
                int take;
                bool trace;

                lock (m_gate) {
                    if (m_disposed || !Stands(snapshot: in snapshot)) { break; }
                    if (m_yield) {
                        m_listed = false;
                        NoteSeenRelights(cursor: 0, end: 0);
                        return;
                    }
                    lighting = m_lighting!;

                    // A part's whole probes of credit; a part probe waits for the next grant.
                    var traceCredits = ((m_budget.TracePerFrame > 0) ? ((long)m_traceCredits) : long.MaxValue);
                    var relightCredits = ((m_budget.RelightPerFrame > 0) ? ((long)m_relightCredits) : long.MaxValue);
                    var traces = ((m_listTraceCursor < m_listTraces) && (traceCredits > 0));
                    var relights = ((m_listRelightCursor < candidates.Count) && (relightCredits > 0));

                    if (!traces && !relights) {
                        // Both parts are done, or what is left waits for the next grant, where the list resumes.
                        if ((m_listTraceCursor >= m_listTraces) && (m_listRelightCursor >= candidates.Count)) { break; }
                        return;
                    }
                    // A relight the share does not take yet waits behind the traces: one overdue or lighting-stale takes
                    // half the worker's time while traces are owed, and any other follows them.
                    trace = traces && (!relights || ((m_listRelightCursor >= m_listUrgent) ? true : (m_listTraceTicks <= m_listRelightTicks)));
                    if (!trace && (m_listRelightCursor >= m_listUrgent) && (m_listTraceCursor < m_listTraces)) { return; }
                    index = (trace ? m_listTraceCursor : m_listRelightCursor);

                    var end = (trace ? m_listTraces : candidates.Count);
                    var credits = (trace ? traceCredits : relightCredits);

                    take = 0;
                    var most = (trace ? BatchProbes : DrainBatchProbes);

                    while (((index + take) < end) && (take < most) && (take < credits)) { take++; }
                    if (trace) {
                        if (m_budget.TracePerFrame > 0) { m_traceCredits -= take; }
                    } else if (m_budget.RelightPerFrame > 0) {
                        m_relightCredits -= take;
                    }
                }

                var began = Stopwatch.GetTimestamp();

                BeginUnit();
                try { Batch(candidates: candidates, count: take, first: index, lighting: lighting, parallel: parallel, pass: -1, snapshot: in snapshot); } finally { EndUnit(); }
                if (trace) {
                    m_listTraceCursor += take;
                    m_listTraceTicks += (Stopwatch.GetTimestamp() - began);
                } else {
                    m_listRelightCursor += take;
                    m_listRelightTicks += (Stopwatch.GetTimestamp() - began);
                    Volatile.Write(location: ref m_seenRelightCursor, value: m_listRelightCursor);
                }
            }
            m_listed = false;
            NoteSeenRelights(cursor: 0, end: 0);
        }
    }
    // The span of the worker's list that holds the probes the views are owed after a lighting jump (Candidate.Seen, ordered
    // first among the relights), and the relight cursor within it; written by the worker alone.
    private int m_seenRelightCursor;
    private int m_seenRelightEnd;
    private void NoteSeenRelights(int cursor, int end) {
        Volatile.Write(location: ref m_seenRelightEnd, value: end);
        Volatile.Write(location: ref m_seenRelightCursor, value: cursor);
    }
    /// <summary>Gets whether the volume owes its views probes they see that a lighting jump left showing the light before
    /// it: a clock scrubbed or jumped, a lamp a rule switches, a light an author edits. The probe service then takes the
    /// budget's owed allowance for them, as for a view's first light (<see cref="OwesFirstLight"/>), until the worker has
    /// relit every such probe, so what the camera sees relights within frames and the rest follows on the age budget.
    /// Safe to read from any thread.</summary>
    public bool OwesSeenRelight => (Volatile.Read(location: ref m_seenRelightCursor) < Volatile.Read(location: ref m_seenRelightEnd));
    /// <summary>Gets whether the volume owes its views light they show wrongly or not at all: their first light
    /// (<see cref="OwesFirstLight"/>) or the probes a lighting jump left stale (<see cref="OwesSeenRelight"/>).</summary>
    public bool OwesViews => (OwesFirstLight || OwesSeenRelight);
    // A lighting-converged frame's work: every probe of the windows traced against the current geometry where it is not already,
    // then the whole window relit from no bounce, each later pass relighting every probe from the bounce the one before
    // left, until a whole pass moves no probe past the convergence bound (or MaxSweeps passes), so the light it renders
    // is a function of its scene, lighting and windows alone and never of the bounce earlier frames left in the cache.
    // The drain keeps the geometry, lighting, windows and layout it snapshot as it started. When any of them changes it
    // stops at its next batch and records nothing drained, so the next converging frame drains again from the new ones;
    // that drain's first pass relights from no bounce, so nothing this one landed reaches its light.
    private void Drain() {
        if (TakeSnapshot() is not { } snapshot) { return; }
        using var sceneBorrow = snapshot.Scene;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = DrainWorkers };
        var candidates = m_candidates;

        m_listed = false;
        NoteSeenRelights(cursor: 0, end: 0);

        BeginUnit();
        try { _ = List(ages: null, all: true, candidates: candidates, snapshot: snapshot); } finally { EndUnit(); }
        for (var pass = 0; (pass < MaxSweeps); pass++) {
            bool moved;

            lock (m_gate) {
                m_sweepMoved = false;
                (m_drainPass, m_drainDone, m_drainTotal) = (pass, 0, candidates.Count);
            }
            for (var index = 0; (index < candidates.Count); index += DrainBatchProbes) {
                lock (m_gate) {
                    if (!DrainStands(snapshot: in snapshot)) { return; }
                    m_drainDone = index;
                }
                BeginUnit();
                try { Batch(candidates: candidates, count: Math.Min(val1: DrainBatchProbes, val2: (candidates.Count - index)), first: index, lighting: snapshot.Lighting, parallel: parallel, pass: pass, snapshot: in snapshot); } finally { EndUnit(); }
            }
            // A probe with no front hit reads no neighbour. Its first pass already computed its final light.
            if (pass == 0) {
                var hold = m_blocks.Enter();

                try {
                    var kept = 0;

                    for (var index = 0; (index < candidates.Count); index++) {
                        var candidate = candidates[index];

                        if (m_levels[candidate.Level].Cache.TryGetValue(candidate.Cell, out var probe, hold: in hold) && probe.ReceivesSurface) { candidates[kept++] = candidate; }
                    }
                    candidates.RemoveRange(index: kept, count: (candidates.Count - kept));
                } finally {
                    hold.Dispose();
                }
                for (var index = 0; index < candidates.Count; index++) { candidates[index] = candidates[index] with { Trace = false }; }
            }
            lock (m_gate) { moved = m_sweepMoved; }
            if (candidates.Count == 0 || !(snapshot.Lighting.Feedback && snapshot.Layout.Bounce) || ((pass > 0) && !moved)) { break; }
        }
        lock (m_gate) {
            m_sweepMoved = false;
            if (!DrainStands(snapshot: in snapshot)) { return; }
            m_drained = DrainKey();
            m_nextLightCheck = 0;
            m_owed = false;
            m_traceOwed = false;
            m_ageMedian = m_ageMax = 0f;
            Report(snapshot: in snapshot);
        }
    }
    private void Report(in Snapshot snapshot) {
        if (m_reported) { return; }

        var probes = 0;

        foreach (var level in m_levels) { probes += level.Cache.Count; }
        m_reported = true;
        Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-gi] done probes={probes} traced={m_traced} relit={m_relit} rays={m_rays} triangles={snapshot.Scene.TriangleCount} lights={snapshot.Lighting.Points.Length} seconds={m_clock.Elapsed.TotalSeconds:0.000}"));
    }
    // Lists eligible probes, every one for a drain. The shared order puts trace demand first, then the probes the views
    // are owed after a lighting jump (Candidate.Seen), then relight age relative to its bound with view/change weights,
    // level, capture age and Morton bands. Returns whether lighting or a
    // deferred phase still owes work, preventing a partial sweep from declaring convergence.
    private bool List(in Snapshot snapshot, bool all, List<Candidate> candidates, List<float>? ages) {
        // Keep one phase for the entire sweep. An empty listing or a later lighting capture must not fill
        // the deferred coarse phases before the finest level can begin its next sweep.
        if (all) {
            m_selectionSweep = 0;
            m_selectionLighting = snapshot.Lighting.Version;
            m_interleaving = false;
        } else if (m_selectionSweep != snapshot.Sweep) {
            m_interleaving = (m_selectionLighting != snapshot.Lighting.Version);
            m_selectionLighting = snapshot.Lighting.Version;
            m_selectionSweep = snapshot.Sweep;
        } else if (m_selectionLighting != snapshot.Lighting.Version) {
            // Lighting replaced within a sweep makes its stale probes due at once, the coarse levels by phase; the sweep
            // keeps the lighting it started under, so the next one interleaves too.
            m_interleaving = true;
        }
        var interleave = m_interleaving;
        m_inputChanges.Clear();
        m_listingLightCheck = 0;

        if (ages is not null) {
            ages.Clear();
            Array.Clear(array: m_listAgeMax);
            Array.Clear(array: m_listAgeCount);
        }
        if (GpuTrace is not null) { return ListGpu(ages: ages, all: all, candidates: candidates, interleave: interleave, snapshot: in snapshot); }
        var stale = false;
        var layout = snapshot.Layout;
        var lighting = snapshot.Lighting;
        // The hold opens before the words are taken, so a replacement cannot free them while this list reads them.
        using var hold = m_blocks.Enter();
        VolumeWords records;
        lock (m_gate) {
            if (!Stands(snapshot)) { throw new OperationCanceledException(); }
            records = m_volume;
        }
        var now = Time.GetTimestamp();

        candidates.Clear();
        for (var level = layout.Finest; (level < m_levels.Length); level++) {
            var cache = m_levels[level].Cache;
            var low = snapshot.Lows[level];
            var dims = layout.DimsOf(level: level);
            var spacing = layout.SpacingOf(level: level);

            for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                    for (var x = low.X; (x < (low.X + dims.X)); x++) {
                        var present = cache.TryGetValue(key: (x, y, z), value: out var probe, hold: in hold);
                        var trace = (!present || ((probe.Geometry != snapshot.Geometry) && Affected(cell: (x, y, z), layout: layout, probe: in probe, snapshot: in snapshot, spacing: spacing)));
                        var rebase = (present && !trace && (probe.Geometry != snapshot.Geometry));
                        var center = (new Vector3(x: (x + 0.5f), y: (y + 0.5f), z: (z + 0.5f)) * spacing);
                        var change = ChangeOf(in probe, lighting, spacing, center, snapshot.Scene, m_inputChanges);
                        var current = change == InputChange.None;
                        var priority = ProbePriority((x, y, z), low, center, snapshot.Camera, spacing);
                        var age = ((!present || current) ? 0 : (float)Time.GetElapsedTime(StaleSince(lighting: probe.Lighting, snapshot: in snapshot), now).TotalSeconds);
                        var bound = SdfMeshProbeRelight.AgeBoundOf(level: level, target: snapshot.AgeTarget);

                        if (present) {
                            RecordAge(age: age, ages: ages, level: level, changed: !current);
                        }
                        var slot = layout.OffsetOf(level) + SlotOf(x, y, z, dims);
                        var required = trace || rebase || !Holds(records.Words[(slot * SdfMeshIrradianceProbe.ProbeWords)..], (x, y, z));
                        if (!required && !OwesRelight(in probe, change, in snapshot, all, interleave, level, age, bound, ProbePhase(x, y, z), now, ref stale)) { continue; }
                        var visible = InAnyView(center, in snapshot);
                        candidates.Add(item: new Candidate(Cell: (x, y, z), Band: priority.Band, Morton: priority.Morton, Level: level, LitAt: (present ? probe.LitAt : 0L), Trace: trace,
                            Overdue: ((bound > 0) && (age >= bound)), Stale: (present && !current), Urgency: AgePriority(age, bound, visible, probe.Moved), Visible: visible,
                            Seen: (visible && present && (change == InputChange.Due) && (probe.Lighting < lighting.Jump))));
                    }
                }
            }
        }
        OrderCandidates(all: all, candidates: candidates);
        return stale;
    }
    private static void OrderCandidates(List<Candidate> candidates, bool all) {
        if (all) {
            candidates.Sort(comparison: static (a, b) => ((a.Level != b.Level) ? a.Level.CompareTo(value: b.Level) : CompareSpatial(a: a, b: b)));
            return;
        }
        candidates.Sort(comparison: static (a, b) => {
            if (a.Trace != b.Trace) { return a.Trace ? -1 : 1; }
            if (!a.Trace) {
                if (a.Seen != b.Seen) { return a.Seen ? -1 : 1; }
                if (a.Overdue != b.Overdue) { return a.Overdue ? -1 : 1; }
                if (a.Stale != b.Stale) { return a.Stale ? -1 : 1; }
                if (a.Urgency != b.Urgency) { return b.Urgency.CompareTo(a.Urgency); }
                if (!a.Overdue && a.Visible != b.Visible) { return a.Visible ? -1 : 1; }
            }
            if (a.Level != b.Level) { return a.Level.CompareTo(b.Level); }
            if (!a.Trace && a.LitAt != b.LitAt) { return a.LitAt.CompareTo(b.LitAt); }
            return CompareSpatial(a, b);
        });
    }
    private static int AgePriority(float age, float bound, bool inView, bool changed) {
        var relative = ((bound > 0) ? (age / bound) : age);
        // Overdue probes ignore view/change weights. Below the bound these weights only reorder work; none retires.
        var weight = (((bound > 0) && (age >= bound)) ? 1 : ((inView ? 1 : .25f) * (changed ? 1.25f : 1)));

        return ((int)Math.Clamp(Math.Floor(d: ((((double)relative) * weight) * 8)), 0, int.MaxValue));
    }
    private void RecordAge(List<float>? ages, int level, float age, bool changed) {
        if (ages is null) { return; }
        ages.Add(item: age);
        m_listAgeMax[level] = MathF.Max(x: m_listAgeMax[level], y: age);
        if (changed) { m_listAgeCount[level]++; }
    }
    private static int ProbePhase(int x, int y, int z) => (x & 1) | ((y & 1) << 1) | ((z & 1) << 2);
    private static int CompareSpatial(Candidate a, Candidate b) => ((a.Band != b.Band) ? a.Band.CompareTo(value: b.Band) : a.Morton.CompareTo(value: b.Morton));
    // Eight-cell radial bands retain near-first demand while adjacent probes share hierarchy cache lines.
    // Relative window coordinates avoid signed-cell wrap at zero; the existing Maths interleave supplies Morton order.
    private static (int Band, uint Morton) ProbePriority((int X, int Y, int Z) cell, (int X, int Y, int Z) low, Vector3 center, Vector3 camera, Vector3 spacing) =>
        (((int)(((center - camera) / spacing).Length() / 8)), BinaryIntegerFunctions.BitwiseTriple<uint, uint>(second: ((uint)(cell.Y - low.Y)), third: ((uint)(cell.Z - low.Z)), value: ((uint)(cell.X - low.X))));
    // When a probe's lighting went stale: the capture of the version after its own, or the oldest capture remembered.
    private long StaleSince(int lighting, in Snapshot snapshot) {
        var index = ((lighting + 1) - snapshot.VersionBase);

        return ((snapshot.VersionTimes.Length == 0) ? Time.GetTimestamp() : snapshot.VersionTimes[Math.Clamp(value: index, min: 0, max: (snapshot.VersionTimes.Length - 1))]);
    }
    // Records the lighting age a listing found and the relight rate of the cycle it closes, and reports a cycle whose
    // oldest lighting is past the budget's age target; under the gate.
    private void Measure(long now) {
        var ages = m_ages;

        if (ages.Count > 0) {
            ages.Sort();
            m_ageMedian = OrderStatistics.Quantile(ascending: CollectionsMarshal.AsSpan(list: ages), fraction: 0.5);
            m_ageMax = ages[^1];
        } else {
            m_ageMedian = m_ageMax = 0f;
        }

        var seconds = ((m_cycleStart == 0L) ? 0d : Time.GetElapsedTime(endingTimestamp: now, startingTimestamp: m_cycleStart).TotalSeconds);

        if (seconds > 0d) { m_cycleRate = ((float)((Counters.Read(kind: Relit) - m_cycleRelit) / seconds)); }
        m_cycleStart = now;
        m_cycleRelit = Counters.Read(kind: Relit);
        m_behind = false;
        if (m_budget.Age <= 0f) { return; }
        var worst = -1;
        var overrun = 1f;
        var needed = 0f;

        for (var level = 0; (level < m_levels.Length); level++) {
            var bound = SdfMeshProbeRelight.AgeBoundOf(level: level, target: m_budget.Age);

            needed += (m_listAgeCount[level] / bound);
            var ratio = (m_listAgeMax[level] / bound);

            if (ratio > overrun) { worst = level; overrun = ratio; }
        }
        if (worst < 0) { return; }
        m_behind = true;
        Counters.Count(kind: Behind);
        if ((m_behindAt != 0L) && (Time.GetElapsedTime(endingTimestamp: now, startingTimestamp: m_behindAt) < BehindInterval)) { return; }
        m_behindAt = now;

        Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"[sdf.mesh-gi] behind level={worst} age-max={m_listAgeMax[worst]:0.##} target={SdfMeshProbeRelight.AgeBoundOf(level: worst, target: m_budget.Age):0.##} combined-age-p50={m_ageMedian:0.##} probes={ages.Count} relit-per-second={m_cycleRate:0} needed-per-second={needed:0} shortfall-per-second={MathF.Max(x: 0f, y: (needed - m_cycleRate)):0} budget-relight-per-frame={m_budget.RelightPerFrame}"));
    }
    // Traces and relights one batch of the listed probes on the pool, then lands it. The batch reads the cache as it stood
    // before the batch, so its probes' bounce does not depend on the order the pool runs them in. A pass of zero or more
    // is a drain's: zero relights with no bounce. A batch landing after its geometry or windows moved is dropped.
    private void Batch(List<Candidate> candidates, int first, int count, Lighting lighting, ParallelOptions parallel, int pass, in Snapshot snapshot) {
        // The words of blocks no reader can still reach go back to the pool before this batch rents its own.
        _ = m_blocks.Reclaim(release: static item => item.Reclaim());
        var hold = m_blocks.Enter();
        var blocks = new BatchBlocks();

        try {
            BatchHeld(blocks: blocks, candidates: candidates, count: count, first: first, hold: hold, lighting: lighting, parallel: parallel, pass: pass, snapshot: in snapshot);
        } finally {
            hold.Dispose();
            if ((blocks.Hits is not null) || (blocks.Lights is not null)) {
                lock (m_gate) {
                    blocks.Hits?.Release(epochs: m_blocks);
                    blocks.Lights?.Release(epochs: m_blocks);
                }
            }
        }
    }
    // The blocks a batch rented, whose own references it releases once it has landed or been dropped.
    private sealed class BatchBlocks {
        public ProbeBlock? Hits;
        public LightBlock? Lights;
    }
    private void BatchHeld(BatchBlocks blocks, List<Candidate> candidates, int first, int count, Lighting lighting, ParallelOptions parallel, int pass, in Snapshot snapshot, ReaderEpochs<Reclaimable>.Hold hold) {
        var results = m_results;

        if (results.Length < count) {
            results = new ProbeData[Math.Max(val1: BatchProbes, val2: count)];
            m_results = results;
        }

        var traces = false;

        for (var index = 0; (index < count); index++) { traces |= candidates[(first + index)].Trace; }

        // A whole batch's hit block even for a short batch. On the GPU the hit words only pass through the batch
        // (ReleaseHits), so the block borrows a buffer allocated for the batch alone and freed when it ends, as the GPU
        // answers' array is returned (BatchEnded): a volume whose lighting has settled holds neither.
        var blockProbes = Math.Max(val1: BatchProbes, val2: count);
        var borrowed = (traces && (GpuTrace is not null));
        var rentedHits = (borrowed ? ProbeBlock.HitBuffer(allocator: m_allocator, count: blockProbes) : null);

        try {
            BatchTraced(blocks: blocks, candidates: candidates, count: count, first: first, lighting: lighting, parallel: parallel, pass: pass, snapshot: in snapshot, hold: hold,
                results: results, traces: traces, blockProbes: blockProbes, rentedHits: rentedHits);
        } finally {
            if (rentedHits is not null) {
                blocks.Hits?.ReleaseHits();
                rentedHits.Dispose();
            }
            BatchEnded();
        }
    }
    // Gives back the GPU answers' array the batch's selections, traces and relights reused.
    private void BatchEnded() {
        m_gpuAnswer?.Free();
    }
    private void BatchTraced(BatchBlocks blocks, List<Candidate> candidates, int first, int count, Lighting lighting, ParallelOptions parallel, int pass, in Snapshot snapshot,
        ReaderEpochs<Reclaimable>.Hold hold, ProbeData[] results, bool traces, int blockProbes, OwnedBuffer<uint>? rentedHits) {
        var borrowed = (rentedHits is not null);
        var hits = blocks.Hits = (traces ? new ProbeBlock(allocator: m_allocator, count: blockProbes, hits: rentedHits) : null);
        var lights = blocks.Lights = new LightBlock(allocator: m_allocator, count: count, inputs: lighting.Inputs);

        hits?.Charge(volume: this);
        lights.Charge(volume: this);
        var feedback = (lighting.Feedback && snapshot.Layout.Bounce && (pass != 0));
        var layout = snapshot.Layout;
        var lows = snapshot.Lows;
        var scene = snapshot.Scene;
        var geometry = snapshot.Geometry;
        var changes = snapshot.Changes;
        var sweep = snapshot.Sweep;

        ArrayCapacity.EnsureScratch(ref m_gpuFresh, count);
        var fresh = m_gpuFresh;
        var prepared = (traces ? PlaceBatch(block: hits!, candidates: candidates, count: count, first: first, fresh: fresh, geometry: geometry, layout: layout, lows: lows, parallel: parallel, scene: scene) : null);
        var committed = true;

        try {
            _ = Parallel.For(fromInclusive: 0, toExclusive: count, parallelOptions: parallel, body: index => {
                var candidate = candidates[(first + index)];

                results[index] = Process(candidate: candidate, changes: changes, feedback: feedback, geometry: geometry, hits: hits, index: index, layout: layout, lighting: lighting,
                    lights: lights, lows: lows, prepared: prepared?[index], scene: scene, sweep: sweep, hold: in hold);
            });
            if (GpuTrace is not null) { committed = RelightGpu(candidates: candidates, count: count, feedback: feedback, first: first, lighting: lighting, lights: lights, probes: results, snapshot: in snapshot, hold: in hold); }
        } finally {
            if (traces && (GpuTrace is not null)) { FreeUnadoptedGpuHits(candidates: candidates, first: first, fresh: fresh.AsSpan(length: count, start: 0)); }
        }

        if (borrowed || (GpuTrace is not null)) { hits?.ReleaseHits(); }
        if (!committed) { Array.Clear(array: results, index: 0, length: count); return; }

        m_landed.Clear();
        lock (m_gate) {
            if (!Stands(snapshot: in snapshot)) {
                m_gpuVolume = null;
                Array.Clear(array: results, index: 0, length: count);
                return;
            }
            var traced = 0;
            var relit = 0;
            var moved = false;

            for (var index = 0; (index < count); index++) {
                var candidate = candidates[(first + index)];
                var result = results[index];

                // A trace of a cell its slot already holds changes a published probe's surface map, offset or validity:
                // the next publication moves the geometry serial (Publish). A cell no slot held answered no gather yet.
                var held = (candidate.Trace && Holds(level: candidate.Level, cell: candidate.Cell));
                if ((result.Hits is null) || !m_levels[candidate.Level].Cache.TrySet(key: candidate.Cell, value: in result)) { continue; }
                relit++;
                if (pass < 0) { m_drained = default; }
                if (candidate.Trace) { traced++; }
                m_geometryOwed |= held;
                moved |= result.Moved;
                m_landed.Add(item: (candidate.Level, candidate.Cell));
            }
            m_traced += traced;
            m_tracedSinceScene += traced;
            m_sweepMoved |= moved;
            m_tracedLanded |= (traced > 0);
            m_relit += relit;
            m_rays += (((long)traced) * SdfMeshProbeDepth.HitCount);
            Counters.Add(amount: traced, kind: Traced);
            Counters.Add(amount: relit, kind: Relit);
        }
        Array.Clear(array: results, index: 0, length: count);
        StoreLanded();
    }
    // Writes the landed probes into the volume, with no borrowing from neighbours. The worker composes them outside the
    // gate, from the cache, so the frame that publishes the volume composes nothing, and writes them under it unless the
    // window moved meanwhile, when it composes the cells the moved window holds again.
    private void StoreLanded() {
        if (m_landed.Count == 0) { return; }

        while (true) {
            int version;

            m_restoreCells.Clear();
            lock (m_gate) {
                version = m_windowVersion;
                foreach (var (level, cell) in m_landed) {
                    if (InWindow(cell: cell, level: level) && m_levels[level].Cache.ContainsKey(key: cell)) { m_restoreCells.Add(item: (level, cell)); }
                }
            }

            var cells = m_restoreCells.Count;
            // Native, for this round alone, as the refill's words are.
            using var words = OwnedBuffer<uint>.OfLength(allocator: m_allocator, clear: false, length: (cells * SdfMeshIrradianceProbe.ProbeWords));
            var composed = ComposeCells(cells: m_restoreCells, words: words);

            lock (m_gate) {
                if (version != m_windowVersion) { continue; }

                for (var index = 0; (index < composed); index++) {
                    var (level, cell) = m_restoreCells[index];

                    if (!Holds(cell: cell, level: level)) { m_levels[level].Unwritten--; }
                    words.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords)).CopyTo(destination: SlotWords(cell: cell, level: level, gpuWritten: (GpuTrace is not null)));
                }
                m_landed.Clear();

                return;
            }
        }
    }
    // One probe of a batch: its placement and hit buffer, when it lacks a trace against the current geometry, then its
    // relight under the batch's lighting. An invalid probe takes no relight: no view reads it.
    private ProbeData Process(in Candidate candidate, int geometry, (int Geometry, ChangeTree? Tree)[] changes, Layout layout, Lighting lighting, (int X, int Y, int Z)[] lows,
        SdfMeshIrradianceScene scene, bool feedback, ProbeBlock? hits, LightBlock lights, int index, int sweep, ProbeData? prepared, in ReaderEpochs<Reclaimable>.Hold hold) {
        var cache = m_levels[candidate.Level].Cache;
        ProbeData probe;

        if (candidate.Trace) {
            var spacing = layout.SpacingOf(level: candidate.Level);
            var cell = candidate.Cell;
            var centre = (new Vector3(x: (cell.X + 0.5f), y: (cell.Y + 0.5f), z: (cell.Z + 0.5f)) * spacing);

            probe = prepared!.Value;

            if (!probe.Invalid) {
                // The farthest in-window receiver is a spacing diagonal plus relocation and the normal bias.
                SdfMeshProbeDepth.Surfaces(hits: hits!.Hits(index: index), origin: (centre + probe.Offset), reach: SurfaceReachOf(spacing: spacing), scene: scene,
                    sheets: layout.Levels[candidate.Level].Sheets, surfaces: hits.Depth(index: index));
            }
        } else if (!cache.TryGetValue(key: candidate.Cell, value: out probe, hold: in hold)) {
            // Only a drain whose first pass was dropped can list a relight of an untraced probe; nothing lands.
            return default;
        }

        var passes = ((probe.Lighting == lighting.Version) ? (probe.Passes + 1) : 1);
        var previous = probe;

        // A listed probe either traced against this geometry or holds hits no change since its own geometry reaches.
        probe = (probe with { Geometry = geometry, LightIndex = index, Moved = false, Sweep = sweep, Lighting = lighting.Version, LocalVersion = lighting.LocalVersion, LitAt = lighting.CapturedAt, Passes = passes, Shading = lights });
        if (!probe.Invalid && (GpuTrace is null)) {
            var origin = ((new Vector3(x: (candidate.Cell.X + 0.5f), y: (candidate.Cell.Y + 0.5f), z: (candidate.Cell.Z + 0.5f)) * layout.SpacingOf(level: candidate.Level)) + probe.Offset);

            var reuseLocal = ReusesLocal(changes: changes, geometry: geometry, lighting: lighting, origin: origin, previous: in previous, trace: candidate.Trace);

            if (reuseLocal && (previous.Geometry != geometry)) { Counters.Count(kind: LocalKept); }

            if (reuseLocal) {
                previous.Local.CopyTo(destination: lights.Local(index: index));
                previous.Lights.CopyTo(destination: lights.Lights(index: index));
            }
            Relight(feedback: feedback, hits: probe.HitWords, layout: layout, lighting: lighting, lights: lights.Lights(index: index), local: lights.Local(index: index),
                lows: lows, origin: origin, radiance: lights.Radiance(index: index), reuseLocal: reuseLocal, scene: scene, sky: lights.SkyLight(index: index), hold: in hold);
            probe = (probe with { Moved = Moves(trace: candidate.Trace, previous: previous, radiance: lights.Radiance(index: index), sky: lights.SkyLight(index: index)) });
        }
        return probe;
    }
    // Whether a relight keeps the bounce iteration going (Work, Drain): a fresh trace, a probe with no earlier relight, or
    // a change past the convergence bound in either cube a later relight reads about its hits (Incoming: the radiance and
    // the sky light). Convergence is a fixed point of everything a relight reads, so a sweep that changes a sky-light
    // cube alone (an open probe under a new sky, whose radiance has no hit to move) still owes the sweep in which the
    // probes that gather it relight. The one rule for the CPU and the GPU relight.
    private static bool Moves(bool trace, ProbeData? previous, ReadOnlySpan<Vector3> radiance, ReadOnlySpan<Vector3> sky) =>
        (trace || (previous is not { Shading: not null } before) || !Settled(after: radiance, before: before.Radiance) || !Settled(after: sky, before: before.SkyLight));
    // Whether a relight left a cube within ConvergedShare of the one before it in every colour channel of every axis, or
    // within ConvergedFloor nits where a channel is dark: a change of colour at one luminance still moves it.
    private static bool Settled(ReadOnlySpan<Vector3> after, ReadOnlySpan<Vector3> before) {
        for (var axis = 0; (axis < 6); axis++) {
            var now = after[axis];
            var then = before[axis];
            var bound = ((ConvergedShare * Vector3.Max(value1: Vector3.Abs(value: now), value2: Vector3.Abs(value: then))) + new Vector3(value: ConvergedFloor));
            var change = Vector3.Abs(value: (now - then));

            if ((change.X > bound.X) || (change.Y > bound.Y) || (change.Z > bound.Z)) { return false; }
        }
        return true;
    }
    // Reject a buried candidate with a few local rays before tracing its full hit buffer.
    private static bool Blocked(Vector3 origin, SdfMeshIrradianceScene scene, float reach) {
        const int Rays = 32;
        var backFaces = 0;

        for (var ray = 0; (ray < Rays); ray++) {
            var direction = PlacementDirections[ray];

            if (!scene.Intersect(direction: direction, distance: out _, material: out _, maximum: reach, normal: out var normal, origin: origin)) { continue; }
            if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) { backFaces++; }
        }

        return (backFaces > (SdfMeshIrradianceProbe.InsideShare * Rays));
    }
    // A probe's or a camera sample's hit buffer at full reach, and each cube axis's share of the sky its misses see under
    // the hit buffer's quadrature. `inside` receives the share of the rays that met back faces; the measurement is valid
    // while it is at most SdfMeshIrradianceProbe.InsideShare.
    private static bool TraceHits(Vector3 origin, Layout layout, SdfMeshIrradianceScene scene, Span<uint> hits, Span<float> sky, out float inside) {
        _ = SdfMeshProbeDepth.TraceHits(backFaces: out _, hits: hits, maximum: layout.Reach, origin: origin, scene: scene);
        return SummarizeHits(hits: hits, inside: out inside, sky: sky);
    }
    private static bool SummarizeHits(ReadOnlySpan<uint> hits, Span<float> sky, out float inside) {
        var backFaces = 0;

        var directions = SdfMeshProbeDepth.RayDirections;
        var weights = SdfMeshProbeDepth.RayWeights;

        sky.Clear();
        for (var texel = 0; (texel < SdfMeshProbeDepth.HitCount); texel++) {
            if (BitConverter.UInt32BitsToSingle(value: hits[(2 * texel)]) < 0) { backFaces++; }
            if (!float.IsPositiveInfinity(f: BitConverter.UInt32BitsToSingle(value: hits[(2 * texel)]))) { continue; }
            for (var axis = 0; (axis < 6); axis++) {
                sky[axis] += (weights[texel] * MathF.Max(x: 0f, y: Vector3.Dot(vector1: directions[texel], vector2: Axes[axis])));
            }
        }
        for (var axis = 0; (axis < 6); axis++) { sky[axis] /= AxisTotals[axis]; }
        inside = (backFaces / ((float)SdfMeshProbeDepth.HitCount));

        return (inside <= SdfMeshIrradianceProbe.InsideShare);
    }
    // Shades a probe's or a camera sample's hits under a lighting into its radiance cube. The part the static point and
    // spot lights and emission send (`local`) depends only on the lighting's local version, so a relight whose probe
    // holds that cube for the current local version (`reuseLocal`) reads it and traces no shadow ray toward a point
    // light; otherwise it shades it into `local` and, when `lights` is not empty, marks which traced lights its origin
    // sees. The directional lights and the bounce are shaded every relight, and every ray that misses carries the sky's
    // radiance about it into `sky`. The same quadrature weighs the cubes as weighed its sky shares. When `open` is not
    // empty it receives each texel's reflectance its bounce leaves open (Shade's Open, zero for a miss or a back face).
    private void Relight(Vector3 origin, ReadOnlySpan<uint> hits, Layout layout, Lighting lighting, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene,
        bool feedback, Span<Vector3> radiance, Span<Vector3> local, Span<Vector3> sky, bool reuseLocal, Span<uint> lights, in ReaderEpochs<Reclaimable>.Hold hold, Span<Vector3> open = default) {
        var directions = SdfMeshProbeDepth.RayDirections;
        var weights = SdfMeshProbeDepth.RayWeights;

        radiance.Clear();
        sky.Clear();
        open.Clear();
        if (!reuseLocal) { local.Clear(); }
        for (var texel = 0; (texel < SdfMeshProbeDepth.HitCount); texel++) {
            if (!SdfMeshProbeDepth.TryHit(backFace: out var back, distance: out var distance, hits: hits, material: out var material, normal: out var normal, texel: texel)) {
                for (var axis = 0; (axis < 6); axis++) {
                    sky[axis] += (lighting.SkyTexels[texel] * (weights[texel] * MathF.Max(x: 0f, y: Vector3.Dot(vector1: directions[texel], vector2: Axes[axis]))));
                }
                continue;
            }
            if (back) { continue; }

            var direction = directions[texel];

            var (varying, fixedLight, texelOpen) = Shade(feedback: feedback, layout: layout, lighting: lighting, local: !reuseLocal, lows: lows, material: material, normal: normal,
                point: (origin + (direction * distance)), scene: scene, hold: in hold);

            if (!open.IsEmpty) { open[texel] = texelOpen; }
            if ((varying == Vector3.Zero) && (fixedLight == Vector3.Zero)) { continue; }
            for (var axis = 0; (axis < 6); axis++) {
                var weight = (weights[texel] * MathF.Max(x: 0f, y: Vector3.Dot(vector1: direction, vector2: Axes[axis])));

                if (weight <= 0f) { continue; }
                radiance[axis] += (varying * weight);
                if (!reuseLocal) { local[axis] += (fixedLight * weight); }
            }
        }
        for (var axis = 0; (axis < 6); axis++) {
            if (!reuseLocal) { local[axis] /= AxisTotals[axis]; }
            radiance[axis] = ((radiance[axis] / AxisTotals[axis]) + local[axis]);
            sky[axis] /= AxisTotals[axis];
        }
        if (lights.IsEmpty) { return; }
        if (!reuseLocal) {
            lights.Clear();
            // A point light's bit is whether the probe sees its position, at any distance and whatever its power or range,
            // so a light switched on or brightened shows at once; one at the probe's origin is seen.
            foreach (var point in lighting.Points) {
                var toward = (point.Position - origin);
                var length = toward.Length();

                if ((length > SdfMeshIrradianceProbe.LightBias) && !scene.ReachesLight(direction: (toward / length), maximum: (length - SdfMeshIrradianceProbe.LightBias), origin: origin)) { continue; }
                lights[(point.Index >> 5)] |= (1u << (point.Index & 31));
            }
        }
        // A shadowing directional's bit is the probe's own ray toward it, at the hits' reach, and a moving sun moves it, so it
        // is marked on every relight, a reused local part's bits included: a view with no cascade reads it for the light's
        // visibility (sdfLightResponse, through sdfMeshLightVisibility).
        for (var light = 0; (light < lighting.Lights.Length); light++) {
            var index = lighting.LightIndices[light];

            if (((uint)index) >= (32u * SdfMeshIrradianceProbe.LightWords)) { continue; }

            var bit = (1u << (index & 31));

            lights[(index >> 5)] &= ~bit;
            if (lighting.Lights[light].CastsShadow && !scene.Unblocked(direction: lighting.Lights[light].Direction, maximum: layout.Reach, origin: origin)) { continue; }
            lights[(index >> 5)] |= bit;
        }
    }
    // The radiance a hit sends back along its ray, in the views pass's units (albedo times the light a surface receives),
    // in two parts. Varying: the directional lights' bounce under a traced shadow and, with feedback, the reflected light
    // of the probes about it. Local, when asked for: the traced point and spot lights' bounce under their windowed
    // falloff, cone and a traced shadow, and its emission. Open: with feedback, the reflectance (times the bounce gain) a
    // hit no probe answers leaves without its bounce, zero where the volume answers (sdfProbeShade's `unanswered`).
    private (Vector3 Varying, Vector3 Local, Vector3 Open) Shade(Lighting lighting, Vector3 point, Vector3 normal, int material, bool feedback, bool local, Layout layout, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene, in ReaderEpochs<Reclaimable>.Hold hold) {
        var surface = lighting.MaterialAt(index: material);
        var reflected = ((surface.Albedo * (1f - surface.Metal)) * (surface.Bleed ?? Vector3.One));
        var varying = Vector3.Zero;
        var fixedLight = Vector3.Zero;
        var lifted = (point + (normal * 1.0e-3f));

        if (lighting.Direct && (reflected != Vector3.Zero)) {
            var direct = Vector3.Zero;

            foreach (var light in lighting.Lights) {
                var lambert = MathF.Max(x: 0f, y: ((Vector3.Dot(vector1: normal, vector2: light.Direction) + surface.Wrap) / (1f + surface.Wrap)));

                if (lambert <= 0f) { continue; }
                if (light.CastsShadow && !scene.Unblocked(direction: light.Direction, maximum: layout.Reach, origin: lifted)) { continue; }
                direct += (light.Color * ((light.Weight * lambert) * light.Bounce));
            }
            varying += ((reflected * direct) * lighting.Gains.Lights);
            if (local) {
                var points = Vector3.Zero;

                foreach (var light in lighting.Points) {
                    var toward = (light.Position - point);
                    var distanceSquared = toward.LengthSquared();

                    if (distanceSquared >= (light.Range * light.Range)) { continue; }

                    var distance = MathF.Sqrt(x: distanceSquared);
                    var direction = (toward / MathF.Max(x: distance, y: 1.0e-4f));
                    var lambert = MathF.Max(x: 0f, y: ((Vector3.Dot(vector1: normal, vector2: direction) + surface.Wrap) / (1f + surface.Wrap)));

                    if (lambert <= 0f) { continue; }

                    var cone = Math.Clamp(value: (Vector3.Dot(vector1: -direction, vector2: light.SpotAxis) + light.SpotOffset), min: 0f, max: 1f);

                    if (cone <= 0f) { continue; }
                    if ((distance > SdfMeshIrradianceProbe.LightBias) && !scene.ReachesLight(direction: direction, maximum: (distance - SdfMeshIrradianceProbe.LightBias), origin: lifted)) { continue; }
                    points += (light.Color * ((((light.Weight * SdfPhotometry.Falloff(distance: distance, range: light.Range)) * (cone * cone)) * lambert) * light.Bounce));
                }
                fixedLight += ((reflected * points) * lighting.Gains.Lights);
            }
        }
        // A lane-driven emissive reads its lane from a frame slot, and a static draw has none: it reads zero, as the views
        // pass shades it.
        if (local && lighting.Emission && (surface.Emissive > 0f) && (surface.EmissiveLane is null)) {
            fixedLight += (((surface.Albedo * surface.Emissive) * (surface.Bleed ?? Vector3.One)) * lighting.Gains.Emission);
        }
        var open = Vector3.Zero;

        if (feedback && (reflected != Vector3.Zero)) {
            if (TryIncoming(layout: layout, lows: lows, normal: normal, point: point, hold: in hold, light: out var incoming)) {
                varying += ((reflected * incoming) * lighting.Gains.Feedback);
            } else {
                open = (reflected * lighting.Gains.Feedback);
            }
        }

        return (varying, fixedLight, open);
    }
    // The light a surface receives from the cached probes about it, as the views pass reads the volume
    // (sdfMeshProbesAt, shade/sdf-mesh-irradiance.hlsli): the probes Select chooses, each weighted as Gather weighs it.
    // It reads each probe's own cubes, exactly as the published volume does.
    private bool TryIncoming(Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z)[] lows, in ReaderEpochs<Reclaimable>.Hold hold, out Vector3 light) {
        var (sky, bounce, answers) = IncomingParts(layout: layout, lows: lows, normal: normal, point: point, hold: in hold);

        light = (sky + bounce);
        return answers;
    }
    // TryIncoming's two terms apart: the sky light and the surfaces' radiance the probes' cubes hold, and whether any probe
    // answers the receiver (sdfMeshAnswers), both terms zero where none does.
    private (Vector3 Sky, Vector3 Bounce, bool Answers) IncomingParts(Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z)[] lows, in ReaderEpochs<Reclaimable>.Hold hold) {
        var squared = (normal * normal);
        Span<float> cornerWeights = stackalloc float[GatherSlots];
        Span<(int X, int Y, int Z)> cornerCells = stackalloc (int X, int Y, int Z)[GatherSlots];
        var cornerProbes = new Corners();

        _ = Select(cells: cornerCells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, layout: layout, lows: lows, normal: normal, point: point, hold: in hold);

        var sky = Vector3.Zero;
        var bounce = Vector3.Zero;
        var total = 0f;

        for (var corner = 0; (corner < GatherSlots); corner++) {
            if (cornerWeights[corner] <= 0f) { continue; }

            var probe = cornerProbes[corner];

            sky += (Cube(cube: probe.SkyLight, normal: normal, squared: squared) * cornerWeights[corner]);
            bounce += (Cube(cube: probe.Radiance, normal: normal, squared: squared) * cornerWeights[corner]);
            total += cornerWeights[corner];
        }

        if (total <= 0f) {
            return (Vector3.Zero, Vector3.Zero, false);
        }

        return ((sky / total), (bounce / total), true);
    }

    /// <summary>Reads the probes about a receiver as the views pass gathers them (<c>sdfMeshProbesAt</c>): the chosen
    /// stage's eight corners, then the remainder stage's eight, each corner's cell and its weight after facing and depth
    /// visibility scaled by its stage's share, so the weights sum to one, zero for a corner that does not answer. Each
    /// stage's lookup clamps to its level's window's boundary cells, and the visibility tests the receiver pushed
    /// <see cref="SdfMeshIrradianceProbe.NormalBias"/> metres off its surface.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <param name="cells">Receives the slots' cells, corner <c>c</c> of a cage at offset <c>(c &amp; 1, c &gt;&gt; 1 &amp; 1,
    /// c &gt;&gt; 2)</c> from its lookup's first cell; at least <see cref="GatherSlots"/> long.</param>
    /// <param name="weights">Receives the slots' weights; at least <see cref="GatherSlots"/> long.</param>
    /// <returns><see langword="false"/> while the volume's published geometry is still changing.</returns>
    public bool TryGather(Vector3 point, Vector3 normal, Span<(int X, int Y, int Z)> cells, Span<float> weights) =>
        TryGather(cells: cells, normal: normal, point: point, reports: default, selection: out _, weights: weights);
    /// <summary>Reads the probes about a receiver as <see cref="TryGather(Vector3, Vector3, Span{ValueTuple{int, int, int}}, Span{float})"/>
    /// does, and reports each slot's record and the terms of its weight, and which stages answer.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <param name="cells">Receives the slots' cells; at least <see cref="GatherSlots"/> long.</param>
    /// <param name="weights">Receives the slots' weights; at least <see cref="GatherSlots"/> long.</param>
    /// <param name="reports">Receives each slot's report, or empty for none; otherwise at least <see cref="GatherSlots"/> long.</param>
    /// <param name="selection">Receives the stages that answer.</param>
    /// <returns><see langword="false"/> while the volume's published geometry is still changing.</returns>
    public bool TryGather(Vector3 point, Vector3 normal, Span<(int X, int Y, int Z)> cells, Span<float> weights, Span<SdfMeshProbeReport> reports, out SdfMeshProbeSelection selection) {
        lock (m_gate) {
            selection = default;
            if ((m_lighting is null) || !m_windowValid || GeometrySettling || Dirty) { return false; }
            var probes = new Corners();
            using var hold = m_blocks.Enter();

            selection = Select(cells: cells, cornerProbes: ref probes, cornerWeights: weights, layout: m_layout, lows: Lows(), normal: normal, point: point, reports: reports, hold: in hold);

            return true;
        }
    }
    /// <summary>Reads a traced static light's or a shadowing directional's visibility using the published gather, as the
    /// views shader does (a directional's only in a view with no cascade): the
    /// weight of the slots read bounded by their probes' measured reach whose probe sees the light, so it scales with
    /// the measured share of the answer and is zero where no measured probe answers.</summary>
    /// <param name="point">The receiver position.</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <param name="lightIndex">The lights-table index.</param>
    /// <returns>The weighted visible share.</returns>
    public float LightVisibility(Vector3 point, Vector3 normal, int lightIndex) {
        if (((uint)lightIndex) >= (32u * SdfMeshIrradianceProbe.LightWords)) { return 1f; }
        lock (m_gate) {
            Span<(int X, int Y, int Z)> cells = stackalloc (int, int, int)[GatherSlots];
            Span<float> weights = stackalloc float[GatherSlots];
            var probes = new Corners();
            using var hold = m_blocks.Enter();
            var selection = Select(point: point, normal: normal, layout: m_layout, lows: Lows(),
                cells: cells, cornerWeights: weights, cornerProbes: ref probes, hold: in hold);
            var visible = 0f;

            for (var corner = 0; (corner < GatherSlots); corner++) {
                if ((weights[corner] <= 0f) || !((corner < 8) ? selection.Measured : selection.RemainderMeasured)) { continue; }
                if ((probes[corner].Lights[(lightIndex >> 5)] & (1u << (lightIndex & 31))) != 0u) { visible += weights[corner]; }
            }
            return visible;
        }
    }
    /// <summary>Describes the probes about a receiver as the views pass gathers them: the level the chosen stage reads,
    /// whether it reads its probes bounded by their measured reach, its share of the answer, the remainder stage's level
    /// and boundedness, and the chosen level's spacing and window, then one line a slot with its cell, where its probe
    /// stands (its centre plus the offset the trace moved it by), the share of its rays that met back faces, whether it is
    /// valid, the relights it holds under the current lighting, and the terms of its weight at the receiver (trilinear
    /// share, facing, the depth visibility, whether it lies in front) with the weight it answers with. The remainder's
    /// eight lines follow only where a remainder stage answers. The cubes a relight in progress has not reached yet read
    /// as they were last relit.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <returns>The lines; one line while the volume holds no window to read or its geometry is still changing.</returns>
    public IReadOnlyList<string> DescribeProbes(Vector3 point, Vector3 normal) {
        var cells = new (int X, int Y, int Z)[GatherSlots];
        var weights = new float[GatherSlots];
        var reports = new SdfMeshProbeReport[GatherSlots];
        Layout layout;
        (int X, int Y, int Z) low;
        SdfMeshProbeSelection selection;

        lock (m_gate) {
            if (GeometrySettling) {
                var live = m_levels[0].Low;

                return [string.Create(CultureInfo.InvariantCulture,
                    $"[world.probe: unsettled; published-window=({m_published.Finest.Low.X}, {m_published.Finest.Low.Y}, {m_published.Finest.Low.Z})+{m_published.Finest.Dims.X}x{m_published.Finest.Dims.Y}x{m_published.Finest.Dims.Z} live-window=({live.X}, {live.Y}, {live.Z}); {Describe()}]")];
            }
            if ((m_lighting is null) || !m_windowValid) {
                return [$"[world.probe: no volume at ({point.X:0.##}, {point.Y:0.##}, {point.Z:0.##}); {Describe()}]"];
            }
            layout = m_layout;

            var probes = new Corners();
            using var hold = m_blocks.Enter();

            selection = Select(cells: cells, cornerProbes: ref probes, cornerWeights: weights, layout: layout, lows: Lows(), normal: normal, point: point, reports: reports, hold: in hold);
            low = m_levels[selection.Level].Low;

            var lines = new List<string>(capacity: (2 + GatherSlots));
            var total = 0f;
            var answering = 0;

            foreach (var weight in weights) {
                if (weight > 0f) {
                    total += weight;
                    answering++;
                }
            }
            var level = selection.Level;

            var (sky, bounce, _) = IncomingParts(layout: layout, lows: Lows(), normal: normal, point: point, hold: in hold);

            lines.Add(item: string.Create(provider: CultureInfo.InvariantCulture,
                $"[world.probe: point=({point.X:0.###}, {point.Y:0.###}, {point.Z:0.###}) normal=({normal.X:0.##}, {normal.Y:0.##}, {normal.Z:0.##}) level={level} measured={(selection.Measured ? 1 : 0)} share={selection.Share:0.####} remainder-level={selection.RemainderLevel} remainder-measured={(selection.RemainderMeasured ? 1 : 0)} spacing={SpacingText(spacing: layout.SpacingOf(level: level))} window=({low.X}, {low.Y}, {low.Z})+{layout.DimsOf(level: level).X}x{layout.DimsOf(level: level).Y}x{layout.DimsOf(level: level).Z} answering={answering} weight={total:0.####} relighting={(m_owed ? 1 : 0)} indirect-sky=({sky.X:0.#}, {sky.Y:0.#}, {sky.Z:0.#}) indirect-bounce=({bounce.X:0.#}, {bounce.Y:0.#}, {bounce.Z:0.#})]"));
            lines.Add(item: Describe());
            for (var slot = 0; (slot < ((selection.RemainderLevel >= 0) ? GatherSlots : 8)); slot++) {
                var report = reports[slot];
                var description = report.Describe();

                if (report.Present && !report.Invalid && (m_scene is { } scene)) {
                    var toward = ((point + (normal * SdfMeshIrradianceProbe.NormalBias)) - report.Position);
                    var distance = toward.Length();
                    var clear = ((distance <= 1e-3f) || scene.Unblocked(report.Position, (toward / distance), (distance - 1e-3f)));

                    description = description.Insert(startIndex: (description.Length - 1), value: $" reference-clear={(clear ? 1 : 0)}");
                    if ((GpuTrace is not null) && !m_owed && (probes[slot].Hits is not null)) {
                        description = description.Insert(startIndex: (description.Length - 1), value: DescribeRelightReference(probes[slot], report.Position, scene));
                    }
                }
                lines.Add(item: description);
            }

            return lines;
        }
    }

    // KEEP IN SYNC with sdfMeshProbesAt. Chooses the probes a receiver reads from a chain of stages, finest first: each
    // finer level whose boundary probes' surface maps reach the receiver past its window (SurfaceReachOf), then the level
    // whose window holds it (the finest such, the coarsest past every window) bounded by its probes' measured reach, the
    // same probes unbounded, blocked only by the surfaces they measured, and last the finest level's boundary probes
    // unbounded. A coarser level never answers inside a finer window, where its metres-wide angular support would see
    // through a thin wall a finer probe measured.
    // The first stage whose probes answer is chosen, and it answers only in its share: its corners' weight over the same
    // cage's front weight before visibility, clamped to one. An 8 m level has a probe plane in every other storey, so a
    // single corner it measured across a slab, normalized to the whole, would light a room with another's light. The rest
    // of the answer comes from the next stage over another cage (a later stage of the chosen level reads the same probes,
    // so it adds nothing a share would want), with its weights normalized to that rest; where none answers, the chosen
    // stage's weights are normalized to the whole, the finest level's reads within its window among them. A finer level's
    // stage past its window answers in its share times its reach share (ReachShareOf), so the next stage takes over across
    // the outer half of its reach. Each slot's weight is pre-scaled by its stage's share, so the slots' weights sum to one.
    private SdfMeshProbeSelection Select(Vector3 point, Vector3 normal, Layout layout, ReadOnlySpan<(int X, int Y, int Z)> lows,
        Span<(int X, int Y, int Z)> cells, Span<float> cornerWeights, ref Corners cornerProbes, in ReaderEpochs<Reclaimable>.Hold hold, Span<SdfMeshProbeReport> reports = default) {
        var held = (m_levels.Length - 1);

        for (var level = layout.Finest; (level < held); level++) {
            if (Beyond(layout: layout, level: level, low: lows[level], normal: normal, point: point) <= 0f) {
                held = level;
                break;
            }
        }
        cells[..GatherSlots].Clear();
        cornerWeights[..GatherSlots].Clear();
        if (!reports.IsEmpty) { reports[..GatherSlots].Clear(); }

        var chosen = -1;
        var chosenMeasured = false;
        var chosenTotal = 0f;
        var share = 0f;
        var remainder = -1;
        var remainderMeasured = false;

        var cold = false;
        for (var stage = layout.Finest; stage <= m_levels.Length + 1; stage++) {
            var bounded = stage < m_levels.Length;
            if (bounded && stage > held && !cold) { continue; }
            var level = bounded ? stage : (stage == m_levels.Length ? held : 0);

            if (!layout.Measures(level) || (stage > m_levels.Length && held == 0)) { continue; }

            var reachShare = 1f;

            if (stage < held) {
                var beyond = Beyond(layout: layout, level: level, low: lows[level], normal: normal, point: point);
                var reach = SurfaceReachOf(spacing: layout.SpacingOf(level: level));

                if (beyond > reach) { continue; }
                reachShare = ReachShareOf(beyond: beyond, reach: reach);
            }
            if ((chosen >= 0) && (level == chosen)) { continue; }

            var slot = ((chosen >= 0) ? 8 : 0);
            var total = Gather(cells: cells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, depth: bounded, front: out var front, missing: out var missing, layout: layout, level: level,
                low: lows[level], normal: normal, point: point, reports: reports, slot: slot, hold: in hold);
            if (bounded && level >= held) { cold = missing > 0f; }
            front += missing;

            if (!(total > 0f)) { continue; }
            if (chosen < 0) {
                chosen = level;
                chosenMeasured = bounded;
                chosenTotal = total;
                share = (((front > total) ? (total / front) : 1f) * reachShare);
                if (share >= 1f) { break; }
                continue;
            }
            remainder = level;
            remainderMeasured = bounded;
            Scale(reports: reports, scale: ((1f - share) / total), slot: 8, weights: cornerWeights);
            break;
        }
        if (chosen < 0) { return new SdfMeshProbeSelection(Level: 0, Measured: false, Share: 0f); }
        if (remainder < 0) { share = 1f; }
        Scale(reports: reports, scale: (share / chosenTotal), slot: 0, weights: cornerWeights);
        return new SdfMeshProbeSelection(Level: chosen, Measured: chosenMeasured, RemainderLevel: remainder, RemainderMeasured: remainderMeasured, Share: share);

        static void Scale(int slot, float scale, Span<float> weights, Span<SdfMeshProbeReport> reports) {
            for (var corner = slot; (corner < (slot + 8)); corner++) {
                weights[corner] *= scale;
                if (!reports.IsEmpty) { reports[corner] = (reports[corner] with { Weight = weights[corner] }); }
            }
        }
    }

    /// <summary>Returns the farthest receiver from a probe its surface map measured, in metres: its reach
    /// (<see cref="SdfMeshIrradianceProbe.SurfaceReach"/> cell diagonals) plus the receiver's normal bias.</summary>
    /// <param name="spacing">The probe spacing of the probe's level along each axis, in metres.</param>
    /// <returns>The reach, in metres.</returns>
    public static float SurfaceReachOf(Vector3 spacing) => ((SdfMeshIrradianceProbe.SurfaceReach * spacing.Length()) + SdfMeshIrradianceProbe.NormalBias);

    // The share a finer level's stage keeps of its answer past its window: whole up to SurfaceReachFadeStart of its reach,
    // easing to none at the reach, so the next stage takes the receiver over across that band rather than at a line one
    // probe row tall. KEEP IN SYNC with sdfMeshIrradianceReachShare.
    private static float ReachShareOf(float beyond, float reach) {
        var start = (SdfMeshIrradianceProbe.SurfaceReachFadeStart * reach);
        var eased = Math.Clamp(max: 1f, min: 0f, value: ((beyond - start) / (reach - start)));

        return (1f - ((eased * eased) * (3f - (2f * eased))));
    }
    // How far past a level's boundary probes a receiver pushed along its normal stands, in metres; zero within them.
    private static float Beyond(Layout layout, int level, (int X, int Y, int Z) low, Vector3 normal, Vector3 point) {
        var (unclamped, sample) = Lattice(layout: layout, level: level, low: low, normal: normal, point: point);

        return ((unclamped - sample) * layout.SpacingOf(level: level)).Length();
    }
    // A receiver's lattice coordinate in a level's window, pushed along its normal, unclamped and clamped to the window's
    // boundary probes.
    private static (Vector3 Unclamped, Vector3 Sample) Lattice(Layout layout, int level, (int X, int Y, int Z) low, Vector3 normal, Vector3 point) {
        var spacing = layout.SpacingOf(level: level);
        var dims = layout.DimsOf(level: level);
        var pushed = (point + (normal * SdfMeshIrradianceProbe.NormalBias));
        var unclamped = ((pushed / spacing) - new Vector3(value: 0.5f));
        var sample = Vector3.Clamp(
            value1: unclamped,
            min: new Vector3(x: low.X, y: low.Y, z: low.Z),
            max: new Vector3(x: ((low.X + dims.X) - 1), y: ((low.Y + dims.Y) - 1), z: ((low.Z + dims.Z) - 1))
        );

        return (unclamped, sample);
    }
    // The eight probes of a level about a receiver, into the eight slots from `slot`: each corner's cell, probe and weight
    // after facing and angular surface visibility, bounded by each probe's measured reach when `depth`, zero where the
    // corner does not answer. Returns the corners' total weight, and in `front` the same cage's weight before visibility
    // with the facing fade alone (no behind floor), over every present probe, invalid ones included: the support a share
    // is measured against. A nonempty `reports` receives each corner's record and the terms of its weight.
    private float Gather(int level, Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z) low, bool depth, int slot, Span<(int X, int Y, int Z)> cells, Span<float> cornerWeights, ref Corners cornerProbes, out float front, out float missing, in ReaderEpochs<Reclaimable>.Hold hold, Span<SdfMeshProbeReport> reports = default) {
        var spacing = layout.SpacingOf(level: level);
        var dims = layout.DimsOf(level: level);
        var cache = m_levels[level].Cache;
        var pushed = (point + (normal * SdfMeshIrradianceProbe.NormalBias));

        var (unclamped, sample) = Lattice(layout: layout, level: level, low: low, normal: normal, point: point);
        // How far past the boundary probes the receiver stands, in cells, at most one: the facing term eases to one over
        // that cell, since every boundary probe lies on the window's side of a receiver outside it.
        var outside = Math.Clamp(value: (unclamped - sample).Length(), min: 0f, max: 1f);
        var floor = new Vector3(x: MathF.Floor(x: sample.X), y: MathF.Floor(x: sample.Y), z: MathF.Floor(x: sample.Z));
        var fraction = (sample - floor);
        var total = 0f;

        front = 0f;
        missing = 0f;
        for (var corner = 0; (corner < 8); corner++) {
            var offset = new Vector3(x: corner & 1, y: (corner >> 1) & 1, z: (corner >> 2) & 1);
            var cell = (((int)(floor.X + offset.X)), ((int)(floor.Y + offset.Y)), ((int)(floor.Z + offset.Z)));
            var index = (slot + corner);

            cells[index] = cell;
            cornerWeights[index] = 0f;
            if (!reports.IsEmpty) { reports[index] = new SdfMeshProbeReport(Cell: cell, Position: ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * spacing))); }
            if (
                (cell.Item1 < low.X) || (cell.Item1 >= (low.X + dims.X)) ||
                (cell.Item2 < low.Y) || (cell.Item2 >= (low.Y + dims.Y)) ||
                (cell.Item3 < low.Z) || (cell.Item3 >= (low.Z + dims.Z))
            ) {
                continue;
            }

            var weights = new Vector3(
                x: ((offset.X > 0f) ? fraction.X : (1f - fraction.X)),
                y: ((offset.Y > 0f) ? fraction.Y : (1f - fraction.Y)),
                z: ((offset.Z > 0f) ? fraction.Z : (1f - fraction.Z))
            );
            var trilinear = ((weights.X * weights.Y) * weights.Z);
            if (!cache.TryGetValue(key: cell, value: out var probe, hold: in hold)) { missing += trilinear; continue; }
            // KEEP IN SYNC with sdfMeshProbesAt: continuous positive-hemisphere weight, then depth visibility.
            var probePoint = ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * spacing) + probe.Offset);
            var toward = (probePoint - point);
            // A distance fade turns down probes behind the receiver without the angular pole that a close, relocated
            // probe's squared cosine stamped into flat floors; at the finest level they keep BehindShare, so a receiver
            // whose every visible probe lies behind its plane still reads them. A coarser level's depth test cannot
            // resolve the receiver's own wall, so a probe behind its plane there takes no part. Irradiance is already
            // evaluated along the normal.
            // The fade runs over half the cell's extent along the normal, half the spacing for a cube.
            var ahead = Math.Clamp((Vector3.Dot(vector1: toward, vector2: normal) / (0.5f * (spacing * normal).Length())), 0f, 1f);
            var fade = ((ahead * ahead) * (3f - (2f * ahead)));
            var facing = float.Lerp(amount: outside, value1: MathF.Max(x: fade, y: ((level == 0) ? SdfMeshIrradianceProbe.BehindShare : 0f)), value2: 1f);
            var visibility = (probe.Invalid ? 0f : SdfMeshProbeDepth.Visibility(bounded: depth, finest: (level == 0), surfaces: probe.Depth, toward: (pushed - probePoint)));
            var weight = ((trilinear * facing) * visibility);

            front += (trilinear * float.Lerp(amount: outside, value1: fade, value2: 1f));

            if (!reports.IsEmpty) {
                var sky = 0f;
                var luminance = 0f;

                for (var axis = 0; (axis < 6); axis++) {
                    sky += (probe.Sky[axis] / 6f);
                    luminance += (SdfPhotometry.LuminanceOf(color: probe.Radiance[axis]) / 6f);
                }
                reports[index] = new SdfMeshProbeReport(Cell: cell, Facing: facing, Front: (ahead > 0f), Inside: probe.Inside, Invalid: probe.Invalid, Luminance: luminance,
                    Offset: probe.Offset, Position: probePoint, Present: true, Sky: sky, Passes: probe.Passes, Trilinear: trilinear, Visibility: visibility);
            }
            if (probe.Invalid || (weight <= 0f)) { continue; }

            cornerProbes[index] = probe;
            cornerWeights[index] = weight;
            total += weight;
        }

        if (!reports.IsEmpty) {
            for (var corner = slot; (corner < (slot + 8)); corner++) {
                reports[corner] = (reports[corner] with { Weight = cornerWeights[corner] });
            }
        }
        return total;
    }
    private static Vector3 Cube(ReadOnlySpan<Vector3> cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));

    // Where a probe may stand in its cell apart from its centre, as shares of the spacing: a lattice a quarter of the
    // spacing apart out to the cell's faces, nearest the centre first (Place).
    private static readonly Vector3[] Candidates = BuildCandidates();

    private static Vector3[] BuildCandidates() {
        var candidates = new List<Vector3>(capacity: 124);

        for (var x = -2; (x <= 2); x++) {
            for (var y = -2; (y <= 2); y++) {
                for (var z = -2; (z <= 2); z++) {
                    if ((x == 0) && (y == 0) && (z == 0)) { continue; }
                    candidates.Add(item: new Vector3(x: (0.25f * x), y: (0.25f * y), z: (0.25f * z)));
                }
            }
        }
        // A stable sort: equal lengths keep their lexicographic order.
        return [.. candidates.OrderBy(keySelector: static candidate => candidate.LengthSquared())];
    }
    // An axis-aligned world box.
    private readonly record struct Box(Vector3 Low, Vector3 High);
    // A probe the worker lists: its level and cell, whether it lacks a trace against the current geometry, the capture
    // time of the lighting it was last relit under (zero for none), the relights it holds under the current lighting,
    // and its radial priority band and Morton cell order within that band. Seen: a view sees it and a lighting change past
    // its bound replaced what it shows (InputChange.Due), which the views are owed (OwesSeenRelight).
    private readonly record struct Candidate(int Level, (int X, int Y, int Z) Cell, bool Trace, long LitAt, int Band, uint Morton, bool Overdue, bool Stale, int Urgency, bool Visible, bool Seen = false);
    // One probe: its geometry (where it stands relative to its cell's centre, the share of its rays that met back faces,
    // whether it lies inside geometry, the geometry it was traced against, its hit buffer, sky shares and surface map
    // in a trace batch's ProbeBlock, and the farthest its rays travelled, which every hit lies within), and its light (its radiance and sky-light cubes and traced-light visibility in a relight batch's
    // LightBlock, the lighting version and capture time it was relit under, and the relights it holds under that
    // version, the sweep it was last relit in and whether that relight moved it past the convergence bound). A batch
    // allocates one block of each rather than arrays a probe; a landed block is never written again, so
    // readers outside the gate read it whole.
    private readonly record struct ProbeData(int Geometry, ProbeBlock? Hits, int HitIndex, bool Invalid, Vector3 Offset, float Inside,
        LightBlock? Shading = null, int LightIndex = 0, int Lighting = 0, long LitAt = 0L, int Passes = 0, int Sweep = 0, bool Moved = false, int LocalVersion = 0, uint GpuSlot = 0, ulong GpuToken = 0,
        float Reach = 0f, bool ReceivesSurface = false, ulong OpenSkyGroups = 0) {
        public ReadOnlySpan<float> Sky => Hits!.Sky(index: HitIndex);
        public ReadOnlySpan<uint> HitWords => Hits!.Hits(index: HitIndex);
        public ReadOnlySpan<uint> Depth => Hits!.Depth(index: HitIndex);
        public ReadOnlySpan<Vector3> Radiance => Shading!.Radiance(index: LightIndex);
        public ReadOnlySpan<Vector3> Local => Shading!.Local(index: LightIndex);
        public ReadOnlySpan<uint> Lights => Shading!.Lights(index: LightIndex);
        public ReadOnlySpan<Vector3> SkyLight => Shading!.SkyLight(index: LightIndex);
    }

    // A batch's block storage: words rented from RentedArrays, zeroed, which a batch writes once and the probe cache's
    // entries then name. Its count is the entries of the current tables that name it, plus one its batch holds until it
    // lands; every change of the count is made under the volume's gate. At zero no table the volume publishes names it,
    // so it is retired to the volume's reader epochs, which return its words to the pool only once every hold that could
    // still read it (a reader that took an entry, from a table current then, before the last entry went) has closed: a
    // reader never sees a block's words rewritten by another batch while it reads them.
    // Storage the volume's reader epochs hand back once no hold that could read it is open: a block's words, or a probe
    // cache table no cache publishes any more.
    private abstract class Reclaimable {
        public abstract void Reclaim();
    }
    // The volume's slot words, zeroed, charged to the volume's owner in the row their storage lies in (OwnedBuffer names
    // it). Its words are read only while the volume publishes it or under a hold entered before it was retired.
    private sealed class VolumeWords : Reclaimable {
        public static readonly VolumeWords None = new(allocator: null, owner: null, words: 0);

        private readonly OwnedBuffer<uint> m_words;

        public VolumeWords(IAllocator? allocator, IAllocationObserver? owner, int words) =>
            m_words = OwnedBuffer<uint>.OfLength(allocator: allocator, clear: true, length: words, observer: owner);

        public int Length => m_words.Length;
        public long Bytes => (((long)m_words.Capacity) * sizeof(uint));
        public Span<uint> Words => m_words.GetSpan();
        public ReadOnlyMemory<uint> Memory => m_words.Memory;

        public override void Reclaim() => m_words.Dispose();
    }
    private abstract class ProbeStorage : Reclaimable {
        private int m_references = 1;

        protected ProbeStorage(int length, IAllocator? allocator) => m_floats = OwnedBuffer<float>.OfLength(allocator: allocator, clear: true, length: length);

        protected readonly OwnedBuffer<float> m_floats;
        // The volume whose block bytes this block is counted in (Charge), until it returns its words.
        private SdfMeshIrradiance? m_volume;

        // The bytes the block holds: its object, its words and any hit words it owns.
        public abstract long Bytes { get; }

        // Counts the block's bytes in the volume's running figure (CacheFigures) until it returns its words, so no
        // figure walks the caches' entries.
        public void Charge(SdfMeshIrradiance volume) {
            m_volume = volume;
            _ = Interlocked.Add(location1: ref volume.m_blockBytes, value: Bytes);
        }
        // Moves the volume's figure by a change of the block's bytes made after it was charged.
        protected void Recharge(long change) {
            if (m_volume is { } volume) { _ = Interlocked.Add(location1: ref volume.m_blockBytes, value: change); }
        }

        // Names the block from one more entry; false once it has been retired, when nothing may name it again.
        public bool TryAddReference() {
            if (m_references <= 0) { return false; }
            m_references++;
            return true;
        }
        // Drops one entry's (or the batch's) reference, retiring the block at the last.
        public void Release(ReaderEpochs<Reclaimable> epochs) {
            m_references--;
            if (m_references == 0) { epochs.Retire(item: this); }
        }
        // Returns the words to the pool; the reader epochs call it once no hold can still read them.
        public override void Reclaim() {
            Recharge(change: -Bytes);
            m_volume = null;
            m_floats.Dispose();
        }
        // A borrow of the block's floats, which ends when the block is reclaimed.
        protected Span<float> Floats(int start, int length) => m_floats.GetSpan().Slice(start: start, length: length);
        // The bytes of the block's floats' storage.
        protected long FloatBytes => (((long)m_floats.Capacity) * sizeof(float));
        public static void Release(in ProbeData probe, ReaderEpochs<Reclaimable> epochs) {
            probe.Hits?.Release(epochs: epochs);
            probe.Shading?.Release(epochs: epochs);
        }
        // Names every block of a probe from one more entry, or none when one of them was retired.
        public static bool TryAddReferences(in ProbeData probe, ReaderEpochs<Reclaimable> epochs) {
            if ((probe.Hits is { } hits) && !hits.TryAddReference()) { return false; }
            if ((probe.Shading is { } shading) && !shading.TryAddReference()) {
                probe.Hits?.Release(epochs: epochs);
                return false;
            }
            return true;
        }
    }

    // The geometry words of one trace batch's probes, each probe's at its index: six sky shares, the packed octahedral
    // surface map and curvature, then the hit buffer, held as the floats' bits so a batch is one allocation.
    // `hits`, when given, is a buffer of at least count * HitWords words the block borrows until ReleaseHits; its words
    // are read only after the batch wrote them. Otherwise the block owns its hit words, from the volume's allocator.
    private sealed class ProbeBlock(int count, IAllocator? allocator, OwnedBuffer<uint>? hits = null) : ProbeStorage(length: (count * FloatsPerProbe), allocator: allocator) {
        public const int FloatsPerProbe = (6 + SdfMeshIrradianceProbe.DepthWords);

        private readonly bool m_borrowed = (hits is not null);
        private OwnedBuffer<uint>? m_hits = (hits ?? HitBuffer(allocator: allocator, count: count));

        public override long Bytes => (FloatBytes + ((m_hits is { } hits) ? (((long)hits.Capacity) * sizeof(uint)) : 0L));

        // Hit words for `count` probes, unwritten.
        public static OwnedBuffer<uint> HitBuffer(IAllocator? allocator, int count) => OwnedBuffer<uint>.OfLength(allocator: allocator, clear: false, length: (count * SdfMeshProbeDepth.HitWords));
        public void ReleaseHits() {
            var before = Bytes;

            if (!m_borrowed) { m_hits?.Dispose(); }
            m_hits = null;
            Recharge(change: (Bytes - before));
        }
        public override void Reclaim() {
            ReleaseHits();
            base.Reclaim();
        }
        public Span<float> Sky(int index) => Floats(length: 6, start: (index * FloatsPerProbe));
        public Span<uint> Depth(int index) => MemoryMarshal.Cast<float, uint>(span: Floats(length: SdfMeshIrradianceProbe.DepthWords, start: ((index * FloatsPerProbe) + 6)));
        public Span<uint> Hits(int index) => (m_hits ?? throw new InvalidOperationException(message: "Runtime hit words are resident on the GPU.")).GetSpan().Slice(length: SdfMeshProbeDepth.HitWords, start: (index * SdfMeshProbeDepth.HitWords));
    }
    // The light words of one relight batch's probes, each probe's at its index: the radiance cube (18 floats), the part of
    // it the point and spot lights and emission send (18 floats), its traced-light visibility words, then its sky-light
    // cube (18 floats).
    private sealed class LightBlock(int count, LightInputs inputs, IAllocator? allocator) : ProbeStorage(length: (count * FloatsPerProbe), allocator: allocator) {
        public LightInputs Inputs { get; } = inputs;
        public const int FloatsPerProbe = (54 + SdfMeshIrradianceProbe.LightWords);

        public override long Bytes => FloatBytes;

        public Span<Vector3> Radiance(int index) => MemoryMarshal.Cast<float, Vector3>(span: Floats(length: 18, start: (index * FloatsPerProbe)));
        public Span<Vector3> Local(int index) => MemoryMarshal.Cast<float, Vector3>(span: Floats(length: 18, start: ((index * FloatsPerProbe) + 18)));
        public Span<uint> Lights(int index) => MemoryMarshal.Cast<float, uint>(span: Floats(length: SdfMeshIrradianceProbe.LightWords, start: ((index * FloatsPerProbe) + 36)));
        public Span<Vector3> SkyLight(int index) => MemoryMarshal.Cast<float, Vector3>(span: Floats(length: 18, start: (((index * FloatsPerProbe) + 36) + SdfMeshIrradianceProbe.LightWords)));
    }
    // Table-owned publication arenas: no objects per key. The volume gate serializes writes. Grow/Retain copy
    // stable snapshots into independent arenas; a reader of an old table retains its complete earlier membership.
    // Every entry the current table publishes names its blocks once (ProbeStorage): a write names the new value's blocks
    // before it releases the old value's, Grow moves every entry, and Retain and Clear release each entry they drop once
    // the table without it is published. A read takes an open hold of the volume's reader epochs, so a block a dropped
    // entry named returns to the pool only after every reader that could have taken that entry has finished with it.
    // A table the cache stops publishing (Clear, Retain, Grow) is retired to the same epochs once its entries are released
    // or moved, and comes back as the cache's spare once no hold that could still read it is open, so the next table of
    // its size reuses its arrays instead of allocating them.
    private sealed class ProbeCache(ReaderEpochs<Reclaimable> blocks) {
        private const int InitialCapacity = (1 << 16);

        private Table? m_table;
        private Table? m_spare;
        private int m_capacity = InitialCapacity;

        // A table of the size asked for: the spare, emptied, when it has that size.
        private Table NewTable(int capacity, int entries = 0) {
            var spare = Interlocked.Exchange(location1: ref m_spare, value: null);
            var arena = ((entries == 0) ? (capacity / 2) : entries);

            if ((spare is not null) && (spare.Keys.Length == capacity) && (spare.Values.Capacity == arena)) {
                spare.Reset();
                return spare;
            }
            return new Table(capacity: capacity, owner: this, entries: entries);
        }
        private void Recycle(Table table) => Volatile.Write(location: ref m_spare, value: table);

        // The probes cached.
        public int Count => (Volatile.Read(location: ref m_table)?.Count ?? 0);

        // The current table's bytes: its arena machinery, keys and inline storage. The blocks its entries name are the
        // volume's running figure (ProbeStorage.Charge). The volume gate is held.
        public (long Table, int Capacity) Bytes() {
            if (m_table is not { } table) { return (32, 0); }
            // x64: cache 32, table 56, three array headers 72, plus each key, used flag and dense-entry index.
            var hashStride = (System.Runtime.CompilerServices.Unsafe.SizeOf<(int, int, int)>() + (2 * sizeof(int)));

            return (((((32L + 56L) + 72L) + (table.Keys.LongLength * hashStride)) + table.Values.InlineStorageBytes), table.Keys.Length);
        }
        // Forgets every probe; the next write starts a table of the capacity the cache last reached.
        public void Clear() {
            if (m_table is not { } table) { return; }
            m_capacity = table.Keys.Length;
            Volatile.Write(location: ref m_table, value: null);
            ReleaseEntries(source: table, keep: null, low: default, high: default);
            blocks.Retire(item: table);
        }
        // Releases the blocks of every entry of a table no longer published, except those a kept box still holds.
        private void ReleaseEntries(Table source, Table? keep, (int X, int Y, int Z) low, (int X, int Y, int Z) high) {
            for (var slot = 0; (slot < source.Keys.Length); slot++) {
                if ((source.Used[slot] == 0) || ((keep is not null) && Inside(high: high, key: source.Keys[slot], low: low))) { continue; }
                if (source.Values.TrySnapshot(entry: source.Entries[slot], value: out var value)) { ProbeStorage.Release(epochs: blocks, probe: in value); }
            }
        }
        // Forgets the probes outside a box of cells, from low inclusive to high exclusive, once the cache holds more probes
        // than the box has cells; the volume's gate is held. The kept probes are published as a new table, as Grow publishes
        // one. An old table keeps its earlier key membership and values; retained keys get independent publications.
        // A batch block no kept probe names is released with the forgotten entries once old readers finish.
        public void Retain((int X, int Y, int Z) low, (int X, int Y, int Z) high) {
            if (m_table is not { } table) { return; }

            var cells = ((((long)(high.X - low.X)) * (high.Y - low.Y)) * (high.Z - low.Z));

            if (table.Count <= cells) { return; }

            var kept = 0;

            for (var slot = 0; (slot < table.Keys.Length); slot++) {
                if ((table.Used[slot] != 0) && Inside(high: high, key: table.Keys[slot], low: low)) { kept++; }
            }

            var capacity = InitialCapacity;

            // Reserve the incoming window's cells even after a teleport keeps few or no old probes. Sizing the dense
            // arena from only kept cells would copy the whole hash table at each tiny arena growth while it refills.
            var entries = Math.Max(val1: (kept + 1), val2: ((int)cells));

            capacity = Math.Max(val1: capacity, val2: checked((int)(((ulong)entries) * 2UL).NextPowerOfTwo()));

            var compact = NewTable(capacity: capacity, entries: entries);

            CopyEntries(high: high, low: low, retain: true, source: table, target: compact);
            m_capacity = capacity;
            Volatile.Write(location: ref m_table, value: compact);
            ReleaseEntries(source: table, keep: compact, low: low, high: high);
            blocks.Retire(item: table);
            // A moving window retires a table a move; one no hold can read any more becomes the spare at once.
            _ = blocks.Reclaim(release: static item => item.Reclaim());
        }

        private static bool Inside((int X, int Y, int Z) key, (int X, int Y, int Z) low, (int X, int Y, int Z) high) =>
            ((key.X >= low.X) && (key.X < high.X) && (key.Y >= low.Y) && (key.Y < high.Y) && (key.Z >= low.Z) && (key.Z < high.Z));

        public bool ContainsKey((int X, int Y, int Z) key) => ((Volatile.Read(location: ref m_table) is { } table) && (Find(key: key, table: table) >= 0));
        // A read of a probe's blocks is good for as long as the hold it was taken under stays open.
        public bool TryGetValue((int X, int Y, int Z) key, out ProbeData value, in ReaderEpochs<Reclaimable>.Hold hold) {
            if (!hold.IsOpen) { throw new InvalidOperationException(message: "A probe cache read needs an open hold of the volume's reader epochs."); }
            var table = Volatile.Read(location: ref m_table);
            var slot = ((table is null) ? -1 : Find(key: key, table: table));

            if ((table is null) || (slot < 0)) {
                value = default;

                return false;
            }

            return table.Values.TrySnapshot(entry: table.Entries[slot], value: out value);
        }

        // Writes a cell's entry, naming its blocks first; false, writing nothing, when a block it names was already
        // retired, which a landing whose window stood never meets. The volume's gate is held.
        public bool TrySet((int X, int Y, int Z) key, in ProbeData value) {
            if (!ProbeStorage.TryAddReferences(epochs: blocks, probe: in value)) { return false; }
            var table = m_table;

            if (table is null) {
                table = NewTable(capacity: m_capacity);
                Volatile.Write(location: ref m_table, value: table);
            }

            var slot = Find(key: key, table: table);

            if (slot >= 0) {
                var held = table.Values.TrySnapshot(entry: table.Entries[slot], value: out var previous);

                table.Values.Publish(entry: table.Entries[slot], value: value);
                if (held) { ProbeStorage.Release(epochs: blocks, probe: in previous); }

                return true;
            }
            if ((table.Count + 1) > table.Values.Capacity) {
                table = Grow(table: table);
                slot = Find(key: key, table: table);
            }

            Insert(key: key, slot: ~slot, table: table, value: value);
            return true;
        }

        // Grows dense storage, and the hash table too once half full, before the write that needed it.
        private Table Grow(Table table) {
            var capacity = ((((table.Count + 1) * 2) > table.Keys.Length) ? (table.Keys.Length * 2) : table.Keys.Length);
            var grown = NewTable(capacity: capacity, entries: Math.Min(val1: (capacity / 2), val2: (table.Values.Capacity * 2)));

            CopyEntries(high: default, low: default, retain: false, source: table, target: grown);
            Volatile.Write(location: ref m_table, value: grown);
            blocks.Retire(item: table);

            return grown;
        }
        private static void CopyEntries(Table source, Table target, bool retain, (int X, int Y, int Z) low, (int X, int Y, int Z) high) {
            for (var slot = 0; (slot < source.Keys.Length); slot++) {
                var key = source.Keys[slot];

                if ((source.Used[slot] == 0) || (retain && !Inside(high: high, key: key, low: low))) { continue; }
                _ = source.Values.TrySnapshot(entry: source.Entries[slot], value: out var value);
                Insert(table: target, slot: ~Find(key: key, table: target), key: key, value: value);
            }
        }
        private static void Insert(Table table, int slot, (int X, int Y, int Z) key, in ProbeData value) {
            table.Values.Publish(entry: table.Count, value: value);
            table.Entries[slot] = table.Count;
            table.Keys[slot] = key;
            Volatile.Write(location: ref table.Used[slot], value: 1);
            table.Count++;
        }
        // The slot holding a key, or the complement of the free slot it would take: linear probing from the key's hash,
        // over a table never more than half full.
        private static int Find((int X, int Y, int Z) key, Table table) {
            var mask = (table.Keys.Length - 1);

            for (var slot = Hash(key: key) & mask; ; slot = (slot + 1) & mask) {
                if (Volatile.Read(location: ref table.Used[slot]) == 0) { return ~slot; }
                if (table.Keys[slot] == key) { return slot; }
            }
        }
        private static int Hash((int X, int Y, int Z) key) => unchecked(((key.X * 73856093) ^ (key.Y * 19349663)) ^ (key.Z * 83492791));

        private sealed class Table(int capacity, ProbeCache owner, int entries = 0) : Reclaimable {
            public readonly (int X, int Y, int Z)[] Keys = new (int X, int Y, int Z)[capacity];
            public readonly int[] Used = new int[capacity];
            public readonly int[] Entries = new int[capacity];
            public readonly PublishArena<ProbeData> Values = new(capacity: ((entries == 0) ? (capacity / 2) : entries));

            public int Count;

            // Empties the table for its cache to publish again; no reader can reach it (it came back reclaimed).
            public void Reset() {
                Array.Clear(array: Used);
                Values.Reset();
                Count = 0;
            }
            public override void Reclaim() => owner.Recycle(table: this);
        }
    }
    // One level of the volume: its probes by cell, its window's first cell, the slots of its window still lacking a
    // measurement of their own cell, the slots composed since its last publication, and the window views read. Its
    // state is the volume's gate's, except the cache, which readers take through its published entries.
    private sealed class Level(int index, ReaderEpochs<Reclaimable> blocks) {
        public int Index { get; } = index;
        public ProbeCache Cache { get; } = new(blocks: blocks);
        public MarkedIndexSet Changed { get; } = new();

        public (int X, int Y, int Z) Low;
        public int Unwritten;
        public SdfMeshIrradianceWindow Published;
    }
    // A camera's sample: its hit buffer and sky shares, traced once per cell and geometry, and its radiance cube as last
    // relit; bounded by the frame's view count, not by how far the camera has travelled. Its mutable parts change under
    // the volume's gate.
    private sealed class CameraSample((int X, int Y, int Z) Cell, int Geometry, Vector3 Position) {
        public (int X, int Y, int Z) Cell { get; } = Cell;
        public int Geometry { get; } = Geometry;
        public Vector3 Position { get; } = Position;
        public uint[] Hits { get; } = new uint[SdfMeshProbeDepth.HitWords];
        public float[] Sky { get; } = new float[6];
        public Vector3[] Radiance { get; } = new Vector3[6];
        public Vector3[] Local { get; } = new Vector3[6];
        public Vector3[] SkyLight { get; } = new Vector3[6];

        public bool Invalid { get; set; }
        public int Lighting { get; set; }
        public int LocalVersion { get; set; }
        public bool Settled { get; set; }
    }
    // The probes a read gathers (GatherSlots), held without an allocation on the hot path.
    [System.Runtime.CompilerServices.InlineArray(GatherSlots)]
    private struct Corners {
        private ProbeData m_first;
    }
    // A static point or spot light the probes trace: its index in the lights table, and its record's position, colour,
    // intensity over pi, range, cone and bounce gain.
    private readonly record struct TracedLight(int Index, Vector3 Position, Vector3 Color, float Weight, float Range, Vector3 SpotAxis, float SpotOffset, float Bounce);
    // A tier's volume: its levels (LevelsOf), each measured level's slots after the finer measured levels' in the volume,
    // whether hits reflect the bounce the volume holds about them (any bounce the frame asks for), the reach of its rays
    // (the frame's far distance), and the finest level it measures (SdfFrame.IndirectLevels). A finer level holds no
    // slot: the volume, its device tables and its hit cache hold the measured levels alone, so a world behind a small
    // glass that reads its coarse levels stores those. Such a level is never listed, refilled or published, and the
    // views and the relight read it as unpublished, with no dims (StoredDimsOf) and zero spacing, so the kernels' level
    // base (sdfMeshIrradianceBase) and OffsetOf agree.
    private readonly record struct Layout(SdfMeshIrradianceLevel[] Levels, bool Bounce, float Reach, int Finest) {
        public Vector3 Spacing => Levels[0].Spacing;
        public (int X, int Y, int Z) Dims => Levels[0].Dims;

        // Whether the volume measures a level: one the frame's views read.
        public bool Measures(int level) => (level >= Finest);
        public Vector3 SpacingOf(int level) => Levels[level].Spacing;
        // A level's window size, measured or not: where its window would stand.
        public (int X, int Y, int Z) DimsOf(int level) => Levels[level].Dims;
        // A level's window size as the volume stores and publishes it: none for a level it does not measure.
        public (int X, int Y, int Z) StoredDimsOf(int level) => (Measures(level: level) ? Levels[level].Dims : default);
        // The slots a level holds in the volume: none for a level it does not measure.
        public int CountOf(int level) => (Measures(level: level) ? Levels[level].Count : 0);
        public int OffsetOf(int level) {
            var offset = 0;

            for (var finer = 0; (finer < level); finer++) { offset += CountOf(level: finer); }
            return offset;
        }

        public int TotalCount => OffsetOf(level: SdfMeshIrradianceProbe.Levels);

        public SdfMeshIrradianceWindow WindowOf(int level, (int X, int Y, int Z) low) =>
            new(Dims: DimsOf(level: level), Low: low, Spacing: SpacingOf(level: level));
        // The layout a frame asks for; `levels` is how many levels its views read from the coarsest, zero for every one.
        public static Layout Of(SdfIndirectTier tier, int? bounces, float reach, int levels) => new(
            Bounce: ((bounces ?? 1) > 0),
            Finest: FinestOf(levels: levels),
            Levels: ((tier == SdfIndirectTier.High) ? HighLevels : MediumLevels),
            Reach: reach
        );
    }

    /// <summary>Returns the finest level a volume measures for a frame whose views read <paramref name="levels"/> of its
    /// levels counted from the coarsest (<see cref="SdfFrame.IndirectLevels"/>).</summary>
    /// <param name="levels">The levels read, counted from the coarsest; zero or less, or past
    /// <see cref="SdfMeshIrradianceProbe.Levels"/>, reads every level.</param>
    /// <returns>The finest level measured, zero (the finest of all) for every level.</returns>
    public static int FinestOf(int levels) => (((levels <= 0) || (levels >= SdfMeshIrradianceProbe.Levels)) ? 0 : (SdfMeshIrradianceProbe.Levels - levels));
    // Lighting.Capture's workspace: the frame thread's lists, sky map and sky layer table, which every capture refills.
    private sealed class CaptureScratch {
        public List<SdfLight> Lights { get; } = [];
        public List<int> Indices { get; } = [];
        public List<TracedLight> Points { get; } = [];
        public Vector3[] Map { get; } = new Vector3[SdfSkyEnvironment.Texels];
        public SdfSkyLayer[] Layers { get; } = new SdfSkyLayer[SdfSky.MaxLayers];
        public SdfSkyDetails Details { get; } = new();
    }

    private readonly CaptureScratch m_captureScratch = new();

    // The lighting the probes are relit under: its version and when it was captured, the directional lights, the static
    // point and spot lights, the program's materials, the gains and sources, and the radiance the sky sends along each
    // hit texel's ray.
    private sealed class Lighting {
        public required int Version { get; init; }
        // The version of the part of the lighting the point and spot lights and emission make; it advances only when
        // that part changes, so a relight reuses each probe's local cube across a moving sun.
        public required int LocalVersion { get; init; }
        // The version of the latest capture, this one or an earlier, that replaced the one before it in a jump (Jumps): a
        // probe relit before it and due shows the views light they are owed (OwesSeenRelight). Zero for none.
        public int Jump { get; init; }
        public required long CapturedAt { get; init; }
        public bool Direct { get; init; }
        public bool Emission { get; init; }
        public bool Feedback { get; init; }
        public required SdfIndirectGains Gains { get; init; }
        public required LightInputs Inputs { get; init; }
        public SdfLight[] Lights => Inputs.Directions;
        // Each directional light's index in the frame's lights table, beside it in Lights.
        public required int[] LightIndices { get; init; }
        public required SdfMaterial[] Materials { get; init; }
        public required TracedLight[] Points { get; init; }
        // The appearance the materials were copied from, which a later capture of the same appearance reuses. Never the
        // program: a capture outlives it in pending reads (a camera sample's), and would keep its words alive.
        public required SdfAppearance Appearance { get; init; }
        public bool Sky { get; init; }
        // The sky's radiance about each hit texel's ray, gain applied (SdfMeshProbeDepth.SkyRadiance) and snapped on the
        // authored grid, zero with the sky source off: what a probe's open ray carries, and the only sky a relight or a
        // meter reads, so it is also the capture's identity (SkyMatches).
        public Vector3[] SkyTexels => Inputs.Sky;
        public float SkyPeak { get; init; }

        public SdfMaterial MaterialAt(int index) =>
            (((index >= 0) && (index < Materials.Length)) ? Materials[index] : new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
        // Whether another capture's sky lights the probes as this one's does. Exact: every sky texel equal, so a kept
        // capture relights exactly as the replacement would. Otherwise every texel's every channel within SkyTolerance of
        // its own value in either capture: the relight sums texels with non-negative weights, so no probe's sky light moves
        // by more than that share, even a probe that sees the sky through a few rays, and a dim channel counts however
        // bright the others are.
        public bool SkyMatches(Lighting other, bool exact) {
            if (Sky != other.Sky) { return false; }
            if (exact) { return SkyTexels.AsSpan().SequenceEqual(other: other.SkyTexels); }
            for (var index = 0; (index < SkyTexels.Length); index++) {
                var bound = (Vector3.Max(value1: Vector3.Abs(value: SkyTexels[index]), value2: Vector3.Abs(value: other.SkyTexels[index])) * SkyTolerance);
                var difference = Vector3.Abs(value: (SkyTexels[index] - other.SkyTexels[index]));

                if ((difference.X > bound.X) || (difference.Y > bound.Y) || (difference.Z > bound.Z)) { return false; }
            }
            return true;
        }
        // A directional's turn between two captures that is a jump: the squared chord of five degrees.
        private static readonly float JumpChordSquared = MathF.Pow(x: (2f * MathF.Sin(x: float.DegreesToRadians(degrees: 2.5f))), y: 2f);
        // A directional's relative change of light between two captures that is a jump.
        private const float JumpShare = 0.25f;

        // Whether a capture replaces the one before it in one step past what steady motion brings between two captures: its
        // point and spot lights, emission or materials changed (a lamp a rule switches, a dimmer, an edit), a directional
        // came, went or changed its shadow role, the grid changed, or a directional turned past JumpChordSquared or changed
        // its light by more than JumpShare (a clock scrubbed or jumped). A sun the clock turns moves a fraction of that
        // between captures, so its relight stays on the age budget.
        private static bool Jumps(Lighting previous, LightInputs after, int localVersion) {
            var before = previous.Inputs;

            if ((localVersion != previous.LocalVersion) || (before.Roles != after.Roles) || (before.Grid != after.Grid)) { return true; }

            var delta = LightDelta.Between(after: after, before: before);

            return ((delta.DirectionSquared > JumpChordSquared) || (delta.EnergyShare > JumpShare));
        }
        // Captures a frame's lighting; the materials are the previous capture's while the program is the same one.
        // The workspace is the caller's, refilled by every capture; a capture keeps only copies of what it holds.
        public static Lighting Capture(MultisetDigest roles, SdfFrame frame, SdfLights captureLights, Lighting? previous, int version, int localVersion, CaptureScratch scratch, long capturedAt) {
            var lights = scratch.Lights;
            var indices = scratch.Indices;
            var points = scratch.Points;

            lights.Clear();
            indices.Clear();
            points.Clear();
            var records = captureLights.Records;

            for (var index = 0; (index < frame.Lights.Count); index++) {
                var light = records[index];

                if ((light.Kind == SdfLightKind.Directional) && (light.Weight > 0f)) {
                    lights.Add(item: light with { Direction = SdfLights.UnitDirection(direction: light.Direction) });
                    indices.Add(item: index);
                } else if (light.IsTraced) {
                    // Every traced light, a dark one included: its visibility bits are the geometry's (Relight), and its
                    // range, zero while it is dark, bounds only its bounce.
                    points.Add(item: new TracedLight(Bounce: light.Bounce, Color: light.Color, Index: index, Position: light.Direction, Range: light.Param, SpotAxis: light.SpotAxis, SpotOffset: light.SpotOffset, Weight: light.Weight));
                }
            }

            var skyTexels = new Vector3[SdfMeshProbeDepth.HitCount];
            var sources = frame.IndirectSources;
            var skyPeak = 0f;

            if ((sources & SdfIndirectSources.Sky) != 0) {
                var map = scratch.Map;
                var layers = scratch.Layers;

                frame.Sky.Pack(captureLights, frame.FarDistance, scratch.Details, out var block, layers);
                SdfSkyEnvironment.Render(block: in block, layers: layers, map: map);
                SdfMeshProbeDepth.SkyRadiance(map: map, texels: skyTexels);
                for (var texel = 0; (texel < skyTexels.Length); texel++) { skyTexels[texel] *= frame.IndirectGains.Sky; }
                foreach (var texel in map) {
                    skyPeak = MathF.Max(x: skyPeak, y: SdfPhotometry.LuminanceOf(color: texel));
                }
                frame.IndirectLightingGrid.SnapRadiance(values: skyTexels);
                skyPeak = frame.IndirectLightingGrid.SnapScalar(value: skyPeak);
            }

            var inputs = new LightInputs([.. lights], roles, skyTexels, frame.IndirectLightingGrid);

            return new Lighting {
                Version = version,
                LocalVersion = localVersion,
                Jump = ((previous is null) ? 0 : (Jumps(after: inputs, localVersion: localVersion, previous: previous) ? version : previous.Jump)),
                CapturedAt = capturedAt,
                SkyPeak = skyPeak,
                Direct = ((sources & SdfIndirectSources.Direct) != 0),
                Emission = ((sources & SdfIndirectSources.Emission) != 0),
                Feedback = ((sources & SdfIndirectSources.Feedback) != 0),
                Gains = frame.IndirectGains,
                Inputs = inputs,
                LightIndices = [.. indices],
                Appearance = frame.Program.Appearance,
                Materials = (ReferenceEquals(objA: previous?.Appearance, objB: frame.Program.Appearance) ? previous!.Materials : [.. frame.Program.Appearance.Materials]),
                Points = [.. points],
                Sky = ((sources & SdfIndirectSources.Sky) != 0),
            };
        }
    }
}
