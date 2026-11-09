using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Cameras;
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
public readonly record struct SdfMeshIrradianceParameters(bool Enabled, SdfMeshIrradianceWindow Finest, SdfMeshIrradianceWindow Middle = default, SdfMeshIrradianceWindow Far = default) {
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
/// window at the rate the frame's budget grants (<see cref="SdfFrame.IndirectBudget"/>). Overload lengthens the
/// lighting's age, never the frame, and is reported as an <c>[sdf.mesh-gi] behind</c> line. A lighting-converged frame relights its
/// window from no bounce through every sweep before it renders, so its light depends on its scene, lighting and window
/// alone. The dynamic draws (moving bodies) receive the volume's light and do not cast into it; an anchored light, which
/// moves, is neither traced nor occluded.</para>
/// </summary>
public sealed partial class SdfMeshIrradiance : IDisposable {
    private SdfMeshProbeGpuWork? m_gpuTrace;
    private SdfMeshProbeGpuWork? m_nextGpuTrace;
    private bool m_gpuReattach;

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

    private bool m_disposed;
    private bool m_trimListening;

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
            jobs = new[] { m_build, m_refill, m_worker }.OfType<Task>().ToArray();
        }
        if (jobs.Length == 0) { ReleaseScene(); return; }
        _ = Task.WhenAll(jobs).ContinueWith(continuationAction: _ => ReleaseScene(), scheduler: TaskScheduler.Default);
    }

    private void ReleaseScene() {
        lock (m_gate) { m_scene?.Dispose(); m_scene = null; m_materialsScene = null; m_diagnosticDraws = []; }
    }

    // Each tier's levels, finest first. The finest is the tier's own; the storey level's 2 m vertical spacing puts a probe
    // plane in every storey 2.4 m or taller, which no isotropic coarse level does, over a 128 m by 48 m by 128 m window;
    // the 32 m level reaches past it.
    private static readonly SdfMeshIrradianceLevel StoreyLevel = new(Dims: (32, 24, 32), Sheets: true, Spacing: new Vector3(x: 4f, y: 2f, z: 4f));
    private static readonly SdfMeshIrradianceLevel FarLevel = new(Dims: (32, 12, 32), Sheets: true, Spacing: new Vector3(value: 32f));
    private static readonly SdfMeshIrradianceLevel[] HighLevels = [new(Dims: (48, 16, 48), Sheets: false, Spacing: Vector3.One), StoreyLevel, FarLevel];
    private static readonly SdfMeshIrradianceLevel[] MediumLevels = [new(Dims: (32, 12, 32), Sheets: false, Spacing: new Vector3(value: 2f)), StoreyLevel, FarLevel];

    /// <summary>Returns a tier's levels, finest first: each one's cell size and its window's probes along each axis.</summary>
    /// <param name="tier">The indirect tier.</param>
    /// <returns><see cref="SdfMeshIrradianceProbe.Levels"/> levels.</returns>
    public static ReadOnlySpan<SdfMeshIrradianceLevel> LevelsOf(SdfIndirectTier tier) => ((tier == SdfIndirectTier.High) ? HighLevels : MediumLevels);

    /// <summary>The most probes a volume holds, over every tier's levels.</summary>
    public static readonly int MaxProbes = Math.Max(val1: HighLevels.Sum(selector: static level => level.Count), val2: MediumLevels.Sum(selector: static level => level.Count));

    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-gi";
    /// <summary>The probe slots a gather fills: the chosen stage's eight corners, then the remainder stage's eight.</summary>
    public const int GatherSlots = 16;

    /// <summary>Counts each trace scene built for a change of the static draws.</summary>
    public static readonly WorkKind Scenes = new(name: "sdf.mesh-gi.scenes", unit: "scenes", workClass: WorkClass.Pacing);
    /// <summary>Counts each geometry a scene build built: one no earlier scene held.</summary>
    public static readonly WorkKind GeometriesBuilt = new(name: "sdf.mesh-gi.geometries-built", unit: "geometries", workClass: WorkClass.Pacing);
    /// <summary>Counts each geometry a scene build took from the scene it replaced.</summary>
    public static readonly WorkKind GeometriesReused = new(name: "sdf.mesh-gi.geometries-reused", unit: "geometries", workClass: WorkClass.Pacing);
    /// <summary>Counts the triangles of the geometries scene builds built.</summary>
    public static readonly WorkKind TrianglesBuilt = new(name: "sdf.mesh-gi.triangles-built", unit: "triangles", workClass: WorkClass.Pacing);
    /// <summary>Counts each probe whose hits were traced: its placement and its hit buffer.</summary>
    public static readonly WorkKind Traced = new(name: "sdf.mesh-gi.traced", unit: "probes", workClass: WorkClass.Pacing);
    /// <summary>Counts each probe relight: its radiance cubes and light visibility shaded from its cached hits.</summary>
    public static readonly WorkKind Relit = new(name: "sdf.mesh-gi.relit", unit: "probes", workClass: WorkClass.Pacing);
    /// <summary>Counts rays in completed GPU trace jobs, including placement trials and subsequently invalidated batches.</summary>
    public static readonly WorkKind GpuRays = new(name: "sdf.mesh-gi.gpu-rays", unit: "rays", workClass: WorkClass.Pacing);
    /// <summary>Measured GPU time of completed trace submissions, including their hierarchy upload and readback copy.</summary>
    public static readonly WorkKind GpuTraceNanoseconds = new(name: "sdf.mesh-gi.gpu-trace-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Measured GPU time of completed relight submissions and feedback updates.</summary>
    public static readonly WorkKind GpuRelightNanoseconds = new(name: "sdf.mesh-gi.gpu-relight-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Measured GPU time spent selecting probes owing work.</summary>
    public static readonly WorkKind GpuSelectNanoseconds = new(name: "sdf.mesh-gi.gpu-select-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);
    /// <summary>Queued trace jobs cancelled because their scene was replaced before admission.</summary>
    public static readonly WorkKind GpuCancelled = new(name: "sdf.mesh-gi.gpu-cancelled", unit: "jobs", workClass: WorkClass.Pacing);
    /// <summary>Traversal stack or step-limit assertions; valid scenes must leave this at zero.</summary>
    public static readonly WorkKind TraversalFaults = new(name: "sdf.mesh-gi.traversal-faults", unit: "walks", workClass: WorkClass.Pacing);
    /// <summary>CPU sky captures for probe lighting: each renders the sky map and averages it over the hit texels.</summary>
    public static readonly WorkKind SkyCaptures = new(name: "sdf.mesh-gi.sky-captures", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Counts each relight cycle whose oldest lighting was past the budget's age target.</summary>
    public static readonly WorkKind Behind = new(name: "sdf.mesh-gi.behind", unit: "cycles", workClass: WorkClass.Pacing);
    /// <summary>Counts camera hit traces, including rejected samples inside opaque geometry.</summary>
    public static readonly WorkKind CameraTraces = new(name: "sdf.mesh-gi.camera-traces", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Counts relights of a cached camera sample's hits under a changed lighting.</summary>
    public static readonly WorkKind CameraRelights = new(name: "sdf.mesh-gi.camera-relights", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Counts each exposure meter reading asked of the volume (<see cref="TryMeter"/>).</summary>
    public static readonly WorkKind Meters = new(name: "sdf.mesh-gi.meters", unit: "readings", workClass: WorkClass.Pacing);
    /// <summary>Device time spent gathering or tracing exposure meter samples.</summary>
    public static readonly WorkKind GpuMeterNanoseconds = new(name: "sdf.mesh-gi.gpu-meter-nanoseconds", unit: "nanoseconds", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [Scenes, GeometriesBuilt, GeometriesReused, TrianglesBuilt, Traced, Relit, GpuRays, GpuTraceNanoseconds, GpuRelightNanoseconds, GpuSelectNanoseconds, GpuMeterNanoseconds, GpuCancelled, TraversalFaults, SkyCaptures, Behind, CameraTraces, CameraRelights, Meters];

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// volume exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Gets the volume's counts: its scene builds, the geometry they built and reused, its probe traces and
    /// relights, and its behind cycles.</summary>
    public WorkCounterSet Counters { get; } = new(kinds: DeclaredKinds, name: SourceName);

    private static readonly Vector3[] PlacementDirections = BuildDirections(count: 32);

    private static Vector3[] BuildDirections(int count) {
        var directions = new Vector3[count];

        for (var ray = 0; (ray < count); ray++) { directions[ray] = Fibonacci(count: count, index: ray); }
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

    // The probes one batch takes before it looks again at the camera, the lighting and the budget.
    private const int BatchProbes = 384;
    // A converged frame already waits for the whole window. Larger bounded batches amortize completion/pump latency
    // without submitting the entire High window as one long-running dispatch. CPU reference uses the same boundaries.
    private const int DrainBatchProbes = 2048;

    // The trace's workers: a lighting-converged frame waits for its window traced to the end, so it traces on all but two of the
    // machine's processors; a trace behind rendered frames takes a quarter of them, so the frame thread keeps a core of
    // its own and the memory bandwidth it packs and uploads with.
    private static readonly int DrainWorkers = Math.Max(val1: 1, val2: (Environment.ProcessorCount - 2));
    private static readonly int BackgroundWorkers = Math.Max(val1: 1, val2: (Environment.ProcessorCount / 4));

    /// <summary>The weight every axis of the meter's reading takes besides its facing (<see cref="TryMeter"/>): the
    /// surround a center-weighted meter still reads.</summary>
    public const float MeterSurround = 0.05f;

    // The least wall time between two scene builds while the bake still lands opaque static meshes, so the trace
    // measures what has landed without restarting on every arrival. Presentation only: the scene a frame lights by is
    // never simulation state.
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
    private readonly Level[] m_levels = CreateLevels();

    private static Level[] CreateLevels() {
        var levels = new Level[SdfMeshIrradianceProbe.Levels];

        for (var index = 0; (index < levels.Length); index++) { levels[index] = new Level(index: index); }
        return levels;
    }

    /// <summary>Describes the probe caches the volume keeps across window moves, over every level: their probes, their
    /// tables' capacity and bytes, and the bytes of the hit and light blocks their entries hold. Safe to read from any
    /// thread; a batch landing meanwhile can leave it a block off.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeCache() {
        long table = 0, blocks = 0;
        int capacity = 0, count = 0;

        foreach (var level in m_levels) {
            var (levelTable, levelBlocks, levelCapacity) = level.Cache.Bytes();

            table += levelTable;
            blocks += levelBlocks;
            capacity += levelCapacity;
            count += level.Cache.Count;
        }

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"probe-cache-probes={count} probe-cache-capacity={capacity} probe-cache-table={table}B probe-cache-blocks={blocks}B probe-cache-bytes={(table + blocks)}B trace-scene-cpu-array-bytes={(m_scene?.HierarchyBytes ?? 0)} {GpuTrace?.DescribeMemory()}");
    }

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

    // Where the worker stands in its list, and the geometry, windows and layout the list was made for; the worker's
    // alone, as the list is.
    private bool m_listed;
    private int m_listCursor;
    private int m_listGeometry;
    private int m_listWindow;
    private Layout m_listLayout;
    private int m_listSweep;

    // The worker's store of a batch's landed probes (StoreLanded): the cells that landed, the window cells they restore,
    // and the slot words composed for those.
    private readonly List<(int Level, (int X, int Y, int Z) Cell)> m_landed = [];
    private readonly List<(int Level, (int X, int Y, int Z) Cell)> m_restoreCells = [];
    private uint[] m_restoreWords = [];
    private uint[] m_volume = [];
    private readonly List<int> m_changedSlots = [];
    private readonly Stopwatch m_clock = new();
    private readonly Stopwatch m_sceneClock = new();

    private SdfWorldTables? m_writeTables;
    private Action<ReadOnlyMemory<uint>>? m_write;
    private SdfMeshIrradianceScene? m_scene;

    /// <summary>Acquires the opaque static BVH only when it represents the supplied frame's exact draw inputs.</summary>
    /// <param name="frame">The retained rendered frame.</param>
    /// <returns>The matching scene under a claim the caller disposes, or null while absent or building.</returns>
    public SdfMeshIrradianceScene? DiagnosticScene(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        lock (m_gate) {
            var index = 0;

            foreach (var draw in frame.MeshDraws) {
                if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
                if ((index >= m_diagnosticDraws.Length) || (draw != m_diagnosticDraws[index++])) { return null; }
            }
            return ((index == m_diagnosticDraws.Length) ? m_scene?.Acquire() : null);
        }
    }

    private SdfMeshDraw[] m_diagnosticDraws = [];

    private int m_sceneKey;
    private int m_buildingKey;
    // The static-draw key as StaticKey last folded it, and what it read: the list and its journal, the journal serial and
    // list revision, and per draw whether it was static and its own hash. A frame whose journal names only rows that are
    // and were not static, or that hash as they did, keeps the key without reading the rest of the list.
    private IReadOnlyList<SdfMeshDraw>? m_keyList;
    private Puck.Abstractions.Gpu.GpuChangedRows? m_keyJournal;
    private long m_keySerial;
    private long m_keyRevision;
    private int m_keyValue;
    private int m_keyStatic;

    private int[] m_drawKeys = [];
    private bool[] m_drawStatic = [];
    private readonly Puck.Abstractions.Gpu.GpuUploadRuns m_keyRuns = new(capacity: Puck.Abstractions.Gpu.GpuRegion.MaxCopyRuns);

    private Task? m_build;
    private Lighting? m_lighting;
    private int m_lightingKey;
    private SdfSkyBlock m_lightingSky;

    private readonly SdfSkyLayer[] m_lightingSkyLayers = new SdfSkyLayer[SdfSky.MaxLayers];

    private int m_localKey;
    private int m_localVersion;
    private long m_lightingCheckedAt;
    private int m_lightingVersion;
    private SdfProgram? m_materialsProgram;
    private SdfMeshIrradianceScene? m_materialsScene;
    private int m_materialsKey;

    private readonly SdfSkyLayer[] m_skyLayers = new SdfSkyLayer[SdfSky.MaxLayers];
    private readonly SdfSkyDetails m_skyDetails = new();

    // The volume's geometry: advanced by a scene build, a layout change or a change of the rays' reach, so every probe
    // traced before it that the change may reach traces again; the latest changes, each with the boxes of the static
    // draws it added or removed, null for every probe.
    private int m_geometry;

    private readonly List<(int Geometry, Box[]? Boxes)> m_changes = [];
    // When each recent lighting version was captured, from the version m_versionBase on: a probe's lighting has been
    // stale since the version after its own was captured.
    private readonly List<long> m_versionTimes = [];
    private int m_versionBase = 1;

    private Layout m_layout;
    private SdfMeshIrradianceParameters m_published;
    private SdfIndirectBudget m_budget;
    // The budget's grants not yet spent: probes the worker may relight and trace before the next frame grants more.
    private long m_relightCredits;
    private long m_traceCredits;
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
    private uint[] m_refillWords = [];
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

    /// <summary>Gets whether probes are being traced or relit or wait to reach the volume table: true throughout a
    /// lighting that changes every frame.</summary>
    public bool Settling {
        get {
            lock (m_gate) {
                // A faulted worker settles nothing: the next Stage reports it.
                return ((m_gpuMeterTask is { IsCompleted: false } or { IsFaulted: true }) || (m_worker is { IsCompleted: false } or { IsFaulted: true }) || (m_build is { IsCompleted: false }) || ((m_scene is not null) && m_owed) || m_refillOwed || Dirty || m_wrote);
            }
        }
    }

    // Whether the published geometry is still changing: a scene build, a trace or a window's refill owed. A relight in
    // progress leaves the probes' cells, origins and surface maps as they stand.
    private bool GeometrySettling =>
        ((m_worker is { IsFaulted: true }) || (m_build is { IsCompleted: false }) || ((m_scene is not null) && m_traceOwed) || m_refillOwed || Unwritten ||
        m_tracedLanded || m_geometryWrote);
    private bool Unwritten {
        get {
            foreach (var level in m_levels) {
                if (level.Unwritten > 0) { return true; }
            }
            return false;
        }
    }

    /// <summary>Advances the volume for a frame of the mesh path before the frame's upload: rebuilds the scene when the
    /// static draws changed (while the bake still lands opaque static meshes, from what has landed, once the previous
    /// scene's trace has finished), captures a changed lighting, grants the frame's budget, moves each level's window
    /// with the views' cameras, starts the worker on the probes the windows lack or whose lighting is stale, and writes
    /// the probes that landed into the tables' volume table.</summary>
    /// <param name="frame">The frame, its baked draws dressed in.</param>
    /// <param name="tables">The residency's tables.</param>
    /// <param name="tier">The indirect tier the views asked for; Off reads no volume.</param>
    /// <param name="bakePending">Whether the mesh bake still owes opaque static meshes: a lighting-converged frame then builds no
    /// scene, and any other frame builds one from the meshes landed so far at most every few seconds.</param>
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
    /// <param name="tier">The indirect tier the views asked for; Off reads no volume.</param>
    /// <param name="bakePending">Whether the mesh bake still owes opaque static meshes.</param>
    /// <param name="write">Receives the volume's probe slots, <see cref="SdfMeshIrradianceProbe.ProbeWords"/> words each,
    /// every level's after the finer level's, whenever a frame publishes a change to them.</param>
    /// <returns>The parameters the views pass reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="write"/> is
    /// <see langword="null"/>.</exception>
    public SdfMeshIrradianceParameters Stage(SdfFrame frame, SdfIndirectTier tier, bool bakePending, Action<ReadOnlyMemory<uint>> write) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: write);

        if ((tier == SdfIndirectTier.Off) || (frame.Views.Count == 0)) { return default; }

        lock (m_gate) {
            if (m_gpuReattach) {
                // A replaced device stops its jobs first. Let their coordinator unwind before resetting its allocations.
                if ((m_worker is { IsCompleted: false }) || (m_gpuMeterTask is { IsCompleted: false })) { return default; }
                m_gpuTrace = m_nextGpuTrace;
                m_gpuReattach = false;
                foreach (var owner in m_hitOwners.Values.OrderByDescending(keySelector: owner => owner.Slot)) { m_hitSlots.Free(start: owner.Slot); }
                m_hitOwners.Clear();
                m_gpuVolume = null;
                m_gpuDirty.Clear();
                m_listed = false;
                m_drained = default;
                // The old device's meter is complete; a fault it left belonged to that device.
                m_gpuMeterTask = null;
                m_gpuMeter = null;
                m_gpuMeterAskedOf = null;
                m_changedSlots.Clear();
                foreach (var level in m_levels) { level.Published = default; level.Changed.Clear(); }
                m_published = default;
                // The new tables hold no table and the new service no feedback. The volume's words still hold every
                // measured slot, so the first relight's full snapshot carries them into the feedback, and then every
                // slot republishes at once (RepublishAll) rather than a level publishing slots it never copied.
                m_republishOwed = true;
                Owe();
                m_traceOwed = true;
            }
        }

        // A lighting-converged frame must trace its own scene, not whichever asynchronous build happened to land first.
        // Wait outside the gate: the builder takes it to publish. A build already in flight may belong to an older
        // frame, so resolve again after it lands until this frame's static draws are the installed scene.
        if (frame.ConvergesLighting && !bakePending) {
            while (true) {
                Task? build;

                lock (m_gate) {
                    build = RefreshScene(frame: frame);
                }
                if (build is null) { break; }
                build.GetAwaiter().GetResult();
            }
        }

        var layout = Layout.Of(tier: tier, bounces: frame.IndirectBounces, reach: MathF.Max(x: 1f, y: frame.FarDistance));
        bool drain;

        lock (m_gate) {
            m_wrote = false;
            m_geometryWrote = false;
            if (!bakePending || ScenePendingDue(frame: frame)) { _ = RefreshScene(bakePending: bakePending, frame: frame); }
            RefreshLighting(frame: frame, layout: layout);
            Grant(budget: frame.IndirectBudget);
            m_camera = WindowCamera(frame: frame);
            CaptureViewCameras(frame: frame);
            if (m_cameraSamples.Length != frame.Views.Count) {
                m_cameraSamples = new CameraSample?[frame.Views.Count];
                m_nextCameraSample = 0;
            }

            var resizing = (!m_windowValid || (layout.Levels != m_layout.Levels));

            if (resizing) {
                m_volume = new uint[(layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords)];
                m_changedSlots.Clear();
                foreach (var level in m_levels) {
                    level.Cache.Clear();
                    level.Changed.Clear();
                    level.Published = default;
                }
                m_published = default;
            }
            if (resizing || (layout.Reach != m_layout.Reach)) { AdvanceGeometry(); }
            m_layout = layout;

            var moved = resizing;

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
                OweRefill(pooled: !frame.ConvergesLighting);
            }
            drain = (frame.ConvergesLighting && (m_scene is not null) && (m_lighting is not null) && (m_owed || (m_drained != DrainKey())));
        }

        // A lighting-converged frame refills its window before it renders, waiting for a refill in flight, which takes the gate.
        if (frame.ConvergesLighting) {
            m_refill?.Wait();
            Refill();
        }

        // A lighting-converged frame renders the window traced and relit to the end, so its indirect light and its views' exposure
        // meters never depend on how far asynchronous work had got by the time the frame was asked for, nor on the
        // bounce earlier frames left in the cache. The drain takes the gate itself, so it runs outside it, after the
        // worker in flight, which may hold it, has finished.
        if (drain && (GpuTrace is null)) {
            // The worker returns at its next batch, so the drain takes over whatever budgeted work it had left.
            lock (m_gate) { m_yield = true; }
            try {
                m_worker?.Wait();
            } catch (AggregateException) {
                // A faulted worker is reported below, as it is for any frame.
            }
            lock (m_gate) { m_yield = false; }
            if (m_worker is not { IsFaulted: true }) {
                Drain();
            }
        }

        lock (m_gate) {
            if (m_worker is { IsFaulted: true } faulted) {
                m_worker = null;
                throw new InvalidOperationException(message: "The irradiance volume's trace failed.", innerException: faulted.Exception);
            }
            if (m_gpuMeterTask is { IsFaulted: true } meterFault) {
                m_gpuMeterTask = null;
                throw new InvalidOperationException(message: "The GPU exposure meter failed.", innerException: meterFault.Exception);
            }
            if (drain && (GpuTrace is not null) && !m_gpuDraining) { m_yield = true; }
            if ((m_owed || drain) && (m_scene is not null) && (m_lighting is not null) && (m_worker is null or { IsCompleted: true }) && (Granted || drain)) {
                m_yield = false;
                m_gpuDraining = (drain && (GpuTrace is not null));
                m_worker = StartWorker(drain: drain);
            }
            Publish(layout: layout, write: write);
        }
        ScheduleGpuMeters(frame: frame);
        return m_published;
    }

    // Starts the trace worker. Apart from Stage, whose every call would otherwise allocate the closure over `drain`.
    private Task StartWorker(bool drain) => Task.Run(action: () => {
        try { if (drain && (GpuTrace is not null)) { Drain(); } else { Work(); } } catch (OperationCanceledException) when ((GpuTrace is not null)) { m_listed = false; }
    });
    // What a drain's light is a function of: the geometry, the lighting, the windows and the layout.
    private (int Geometry, int Lighting, int Windows, Layout Layout) DrainKey() => (m_geometry, m_lightingVersion, m_windowVersion, m_layout);

    // Whether the worker may do any work this frame: an unbounded part of the budget, or credit left in either part.
    private bool Granted => ((m_budget.Relight <= 0) || (m_budget.Trace <= 0) || (m_relightCredits > 0) || (m_traceCredits > 0));

    // Grants a frame's budget: each bounded part gains its count, held to two frames' worth, so a worker that falls
    // behind does not bank a burst.
    private void Grant(SdfIndirectBudget budget) {
        budget.Validate();
        m_budget = budget;
        if (budget.Relight > 0) { m_relightCredits = Math.Min(val1: (m_relightCredits + budget.Relight), val2: (2L * budget.Relight)); }
        if (budget.Trace > 0) { m_traceCredits = Math.Min(val1: (m_traceCredits + budget.Trace), val2: (2L * budget.Trace)); }
    }
    // Owes the worker work; the clock the done line reports starts when the volume leaves its settled state.
    private void Owe() {
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
    // trace lands. `changed` holds the world boxes of the static draws the new scene added or removed; null changes
    // every probe (a layout or reach change, or a draw with no box).
    private void AdvanceGeometry(Box[]? changed = null) {
        m_geometry++;
        m_changes.Add(item: (m_geometry, changed));
        if (m_changes.Count > MaxChanges) { m_changes.RemoveAt(index: 0); }
        Owe();
        m_traceOwed = true;
    }
    // Whether a probe traced against an earlier geometry may have other hits or another placement under the current
    // one: some draw a change since added or removed lies in the region its placement searches, or meets one of its
    // rays before the hit the ray recorded. A probe no change can reach keeps its hits, which a trace would reproduce.
    private static bool Affected(in ProbeData probe, (int X, int Y, int Z) cell, Vector3 spacing, Layout layout, in Snapshot snapshot) {
        var changes = snapshot.Changes;

        if ((changes.Length == 0) || (probe.Geometry < (changes[0].Geometry - 1))) { return true; }

        var centre = (new Vector3(x: (cell.X + 0.5f), y: (cell.Y + 0.5f), z: (cell.Z + 0.5f)) * spacing);
        var placement = ((1.5f * spacing) + new Vector3(value: SdfMeshIrradianceProbe.Clearance));
        var origin = (centre + probe.Offset);
        var hits = probe.HitWords;
        var directions = SdfMeshProbeDepth.RayDirections;

        foreach (var (geometry, boxes) in changes) {
            if (geometry <= probe.Geometry) { continue; }
            if (boxes is null) { return true; }
            foreach (var box in boxes) {
                if ((Vector3.Max(value1: box.Low, value2: (centre - placement)) is var low) && (Vector3.Min(value1: box.High, value2: (centre + placement)) is var high) &&
                    (low.X <= high.X) && (low.Y <= high.Y) && (low.Z <= high.Z)) { return true; }

                var toward = (((box.Low + box.High) * 0.5f) - origin);
                var radius = ((box.High - box.Low) * 0.5f).Length();
                var distance = toward.Length();

                if (distance <= radius) { return true; }

                var axis = (toward / distance);
                var cone = MathF.Sqrt(x: (1f - ((radius * radius) / (distance * distance))));

                for (var texel = 0; (texel < SdfMeshProbeDepth.HitCount); texel++) {
                    var direction = directions[texel];

                    if (Vector3.Dot(vector1: direction, vector2: axis) < cone) { continue; }

                    var reach = MathF.Abs(x: BitConverter.UInt32BitsToSingle(value: hits[(2 * texel)]));

                    // A recorded hit on the box's face lies at its entry: the margin keeps rounding from passing it by. The
                    // sheet test reads the scene up to a sheet's thickness behind the hit (SdfMeshProbeDepth.Surfaces).
                    reach = MathF.Min(x: reach, y: layout.Reach);
                    if (Meets(direction: direction, high: box.High, low: box.Low, origin: origin, reach: ((reach + MathF.Max(x: SdfMeshIrradianceProbe.BoxMargin, y: (reach * SdfMeshIrradianceProbe.BoxMarginShare))) + SdfMeshIrradianceProbe.DepthSheetThickness))) { return true; }
                }
            }
        }
        return false;
    }
    // Whether a ray from an origin meets a box within a reach (slab test).
    private static bool Meets(Vector3 origin, Vector3 direction, Vector3 low, Vector3 high, float reach) {
        var inverse = (Vector3.One / direction);
        var near = ((low - origin) * inverse);
        var far = ((high - origin) * inverse);
        var enter = Vector3.Min(value1: near, value2: far);
        var leave = Vector3.Max(value1: near, value2: far);
        var start = MathF.Max(x: MathF.Max(x: enter.X, y: enter.Y), y: MathF.Max(x: enter.Z, y: 0f));
        var end = MathF.Min(x: MathF.Min(x: leave.X, y: leave.Y), y: MathF.Min(x: leave.Z, y: reach));

        return (start <= end);
    }
    // The world boxes of the static draws one draw list holds and the other does not, counted as multisets of their
    // mesh, transform and material; null when one of them has no box.
    private static Box[]? ChangedBoxes(SdfMeshDraw[] before, SdfMeshDraw[] after) {
        var counts = new Dictionary<(SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material), int>(comparer: DrawKeyComparer.Instance);

        foreach (var draw in before) {
            if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
            var key = (draw.Mesh, draw.ObjectToWorld, draw.Material);

            counts[key] = (counts.GetValueOrDefault(key: key) + 1);
        }

        var changed = new List<Box>();

        foreach (var draw in after) {
            if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
            var key = (draw.Mesh, draw.ObjectToWorld, draw.Material);
            var count = counts.GetValueOrDefault(key: key);

            if (count > 0) {
                counts[key] = (count - 1);
                continue;
            }
            if (!TryBox(box: out var box, draw: in draw)) { return null; }
            changed.Add(item: box);
        }
        foreach (var draw in before) {
            if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
            var key = (draw.Mesh, draw.ObjectToWorld, draw.Material);

            if (counts.GetValueOrDefault(key: key) <= 0) { continue; }
            counts[key]--;
            if (!TryBox(box: out var box, draw: in draw)) { return null; }
            changed.Add(item: box);
        }
        return [.. changed];
    }
    private static bool TryBox(in SdfMeshDraw draw, out Box box) {
        if (!SdfMeshBox.TryOf(box: out var oriented, draw: in draw)) {
            box = default;
            return false;
        }

        var extent = oriented.AxisHalf;

        box = new Box(High: ((oriented.Center + extent) + new Vector3(value: SdfMeshIrradianceProbe.BoxMargin)), Low: ((oriented.Center - extent) - new Vector3(value: SdfMeshIrradianceProbe.BoxMargin)));
        return true;
    }
    // Publishes each level whose window has every slot measured: the slots composed since its last publication reach the
    // table in one write with every other ready level's, and its window's coordinates reach the parameters. A level with
    // a slot still unmeasured keeps its previous window and table words, since publishing an empty slot would invent
    // darkness and filling it with sky would invent a leak. The volume is disabled until a level first publishes, and
    // views then light as without it.
    // After a device reattach nothing publishes until RepublishAll has owed every slot.
    private void Publish(Layout layout, Action<ReadOnlyMemory<uint>> write) {
        foreach (var level in m_levels) {
            var window = layout.WindowOf(level: level.Index, low: level.Low);

            if (m_republishOwed || (level.Unwritten > 0) || ((level.Changed.Count == 0) && (level.Published == window))) { continue; }
            m_geometryWrote |= (m_tracedLanded || (level.Published != window));
            m_changedSlots.AddRange(source: level.Changed.Members);
            level.Changed.Clear();
            level.Published = window;
            m_wrote = true;
        }
        if (m_changedSlots.Count > 0) {
            m_tracedLanded = false;
            write(obj: m_volume.AsMemory(length: (layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords), start: 0));
            m_changedSlots.Clear();
        }

        var enabled = false;

        foreach (var level in m_levels) { enabled |= level.Published.Present; }

        m_published = new SdfMeshIrradianceParameters(Enabled: enabled, Far: PublishedOf(layout: layout, level: 2), Finest: PublishedOf(layout: layout, level: 0),
            Middle: PublishedOf(layout: layout, level: 1));
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
    // A level's published window; one not yet published carries its probe counts at zero spacing, so the views pass skips
    // it yet places the coarser levels' slots after its own.
    private SdfMeshIrradianceWindow PublishedOf(Layout layout, int level) => (m_levels[level].Published.Present
        ? m_levels[level].Published
        : new SdfMeshIrradianceWindow(Dims: layout.DimsOf(level: level), Low: default, Spacing: Vector3.Zero));
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
        var anchor = CellOf(position: WindowAnchor(frame: frame, layout: layout, level: level), spacing: spacing);
        var camera = CellOf(position: m_camera, spacing: spacing);
        // The box the window centres on, and the box it keeps in its middle: the same with the first camera.
        var low = anchor;
        var high = anchor;
        var keptLow = Min(a: anchor, b: camera);
        var keptHigh = Max(a: anchor, b: camera);

        for (var index = 1; (index < frame.Views.Count); index++) {
            var view = frame.Views[index];
            var position = view.Camera.Position;

            if (view.Elsewhere || !float.IsFinite(f: position.X) || !float.IsFinite(f: position.Y) || !float.IsFinite(f: position.Z)) { continue; }

            var cell = CellOf(position: position, spacing: spacing);

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
    private static (int X, int Y, int Z) CellOf(Vector3 position, Vector3 spacing) =>
        (((int)MathF.Floor(x: (position.X / spacing.X))), ((int)MathF.Floor(x: (position.Y / spacing.Y))), ((int)MathF.Floor(x: (position.Z / spacing.Z))));
    // Whether cells from low to high fit in a window's middle along one axis.
    private static bool Fits(int low, int high, int dims) => ((high - low) < (dims - (2 * MarginOf(dims: dims))));

    /// <summary>Reads the volume as a center-weighted reflected-light meter at a camera: the luminance the probes about
    /// the camera see along each of their six axes, as last relit, the light the surfaces about it reflect and emit under
    /// every traced light and the sky through the openings it sees, averaged with each axis weighted by the squared cosine
    /// of its angle to where the camera looks plus <see cref="MeterSurround"/>, so the axis ahead counts about four fifths
    /// of the reading. When no visible lattice probe answers, reads a camera sample's hits, traced once per cell and
    /// geometry and relit when the lighting changes. Reuse within a cell requires visibility from the sample's origin.
    /// An anchored light and the screens are no part of the reading.</summary>
    /// <param name="position">The camera's position, in world units.</param>
    /// <param name="forward">The camera's unit forward axis.</param>
    /// <param name="luminance">The metered average luminance, in nits, when the method returns
    /// <see langword="true"/>.</param>
    /// <param name="skyVisibility">The share of the sky the probes about the camera see open, alike in every direction,
    /// in [0, 1], when the method returns <see langword="true"/>: one on open ground, whose probes see the sky over the
    /// upper half of their directions, near zero in a closed room.</param>
    /// <param name="source">Whether the reading came from lattice probes, a camera trace, or neither.</param>
    /// <param name="fresh">Whether the reading must be the camera's own: at a camera cut, a camera sample is reused only
    /// from this very position, never from elsewhere in its cell, so the reading depends on where the camera stands and
    /// not on where it stood before.</param>
    /// <param name="light">Receives, when not empty and the method returns <see langword="true"/>, the light the reading
    /// receives along each of its six axes (+X, -X, +Y, -Y, +Z, -Z), RGB in nits: the radiance and sky-light cubes summed,
    /// as one probe's indirect light, which the views pass reads for a surface no probe answers. At least six long when
    /// not empty.</param>
    /// <returns><see langword="false"/> while no scene is available or the camera lies inside opaque geometry.</returns>
    public bool TryMeter(Vector3 position, Vector3 forward, out float luminance, out float skyVisibility, out SdfExposureMeterSource source, bool fresh = false, Span<Vector3> light = default) {
        luminance = 0f;
        skyVisibility = 1f;
        source = SdfExposureMeterSource.None;
        Counters.Count(kind: Meters);

        lock (m_gate) {
            if (GpuTrace is not null) { return TryGpuMeter(forward: forward, fresh: fresh, light: light, luminance: out luminance, position: position, skyVisibility: out skyVisibility, source: out source); }
            if ((m_lighting is not { } lighting) || !m_windowValid) {
                return false;
            }

            var spacing = m_layout.Spacing;
            var cache = m_levels[0].Cache;
            var sample = ((position / spacing) - new Vector3(value: 0.5f));
            var floor = new Vector3(x: MathF.Floor(x: sample.X), y: MathF.Floor(x: sample.Y), z: MathF.Floor(x: sample.Z));
            var fraction = (sample - floor);
            Span<Vector3> axes = stackalloc Vector3[6];
            var open = 0f;
            var total = 0f;

            for (var corner = 0; (corner < 8); corner++) {
                var offset = new Vector3(x: corner & 1, y: (corner >> 1) & 1, z: (corner >> 2) & 1);
                var cell = (((int)(floor.X + offset.X)), ((int)(floor.Y + offset.Y)), ((int)(floor.Z + offset.Z)));

                if (!cache.TryGetValue(key: cell, value: out var probe) || probe.Invalid) {
                    continue;
                }

                var probePoint = ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * spacing) + probe.Offset);
                var toward = (probePoint - position);
                var distance = toward.Length();

                if ((distance > SdfMeshIrradianceProbe.MeterCoincident) && (m_scene is { } scene) && !scene.Unblocked(direction: (toward / distance), maximum: distance, origin: position)) { continue; }

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

            if (total <= 0f) {
                if (m_scene is not { } scene) { return false; }

                var sampleAtCamera = CameraRadiance(fresh: fresh, lighting: lighting, position: position, scene: scene);

                if (sampleAtCamera.Invalid) { return false; }
                for (var axis = 0; (axis < 6); axis++) {
                    axes[axis] = (sampleAtCamera.Radiance[axis] + sampleAtCamera.SkyLight[axis]);
                    open += (sampleAtCamera.Sky[axis] / 6f);
                }
                total = 1f;
                source = SdfExposureMeterSource.CameraTrace;
            } else {
                source = SdfExposureMeterSource.Probes;
            }
            for (var axis = 0; (axis < 6); axis++) { axes[axis] /= total; }
            luminance = MeteredLuminance(forward: forward, light: axes);
            skyVisibility = Math.Clamp(max: 1f, min: 0f, value: ((SdfMeshIrradianceProbe.OpenGroundShare * open) / total));
            if (!light.IsEmpty) { axes.CopyTo(destination: light); }

            return true;
        }
    }

    // The meter's reading from the light a sample receives along its six axes: each axis's luminance weighted by the
    // squared cosine of its angle to where the camera looks plus MeterSurround. The one reduction of the CPU and the GPU
    // meter's answers.
    private static float MeteredLuminance(ReadOnlySpan<Vector3> light, Vector3 forward) {
        var sum = 0f;
        var weights = 0f;

        for (var axis = 0; (axis < 6); axis++) {
            var ahead = MathF.Max(x: 0f, y: Vector3.Dot(vector1: Axes[axis], vector2: forward));
            var facing = (MeterSurround + (ahead * ahead));

            sum += (SdfPhotometry.LuminanceOf(color: light[axis]) * facing);
            weights += facing;
        }
        return (sum / weights);
    }
    // A camera sample's hits are traced once per cell and geometry, at the camera's own position; its cubes are relit
    // from them when the lighting changes or the volume settles, whose bounce they read. A cell can straddle a partition:
    // a sample belongs to its actual origin, never an assumed open cell centre, and is never written into the volume.
    private CameraSample CameraRadiance(Vector3 position, SdfMeshIrradianceScene scene, Lighting lighting, bool fresh) {
        var cell = CellOf(position: position, spacing: m_layout.Spacing);
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
                Relight(feedback: (lighting.Feedback && m_layout.Bounce), hits: sample.Hits, layout: m_layout, lighting: lighting, lights: default, local: sample.Local,
                    lows: lows, origin: sample.Position, radiance: sample.Radiance, reuseLocal: (sample.LocalVersion == lighting.LocalVersion), scene: scene, sky: sample.SkyLight);
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
        if (valid) {
            Relight(feedback: (lighting.Feedback && m_layout.Bounce), hits: traced.Hits, layout: m_layout, lighting: lighting, lights: default, local: traced.Local,
                lows: lows, origin: position, radiance: traced.Radiance, reuseLocal: false, scene: scene, sky: traced.SkyLight);
        }
        traced.Lighting = lighting.Version;
        traced.LocalVersion = lighting.LocalVersion;
        traced.Settled = !m_owed;
        Counters.Count(kind: CameraTraces);
        m_cameraSamples[m_nextCameraSample] = traced;
        m_nextCameraSample = ((m_nextCameraSample + 1) % m_cameraSamples.Length);
        return traced;
    }

    /// <summary>Describes the volume: its tier's layout, the static scene, the lights it traces, the probes cached, the
    /// work done since it last settled, its budget and its lighting's age.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        lock (m_gate) {
            var buffers = (m_writeTables?.MeshIrradianceBufferBytes ?? default);

            var cached = 0;

            foreach (var level in m_levels) { cached += level.Cache.Count; }

            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-gi] spacing={SpacingText(spacing: m_layout.Spacing)} dims={m_layout.Dims.X}x{m_layout.Dims.Y}x{m_layout.Dims.Z} levels={string.Join(separator: ',', values: m_layout.Levels.Select(selector: static level => SpacingText(spacing: level.Spacing)))} rays={SdfMeshProbeDepth.HitCount} bounce={(m_layout.Bounce ? "on" : "off")} triangles={(m_scene?.TriangleCount ?? 0)} lights={(m_lighting?.Points.Length ?? 0)} cached={cached} lighting-version={m_lightingVersion} local-version={m_localVersion} traced={m_traced} relit={m_relit} traced-rays={m_rays} {DescribeAge()} record-bytes={(SdfMeshIrradianceProbe.ProbeWords * sizeof(uint))} volume-bytes={(m_volume.LongLength * sizeof(uint))} table-device-bytes={buffers.DeviceLocal}");
        }
    }

    // The budget and the lighting's age, as key=value pairs; under the gate.
    private string DescribeAge() => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"budget-relight={((m_budget.Relight > 0) ? m_budget.Relight.ToString(provider: CultureInfo.InvariantCulture) : "unbounded")} budget-trace={((m_budget.Trace > 0) ? m_budget.Trace.ToString(provider: CultureInfo.InvariantCulture) : "unbounded")} age-target={((m_budget.Age > 0f) ? m_budget.Age.ToString(format: "0.##", provider: CultureInfo.InvariantCulture) : "none")} age-p50={m_ageMedian:0.##} age-max={m_ageMax:0.##} relit-per-second={m_cycleRate:0} {DescribeLevelAges()}");
    // Sample at the diagnostic read, rather than reporting the ages from the last work-list rebuild.
    private string DescribeLevelAges() {
        if (m_lighting is null) { return ""; }
        var now = Stopwatch.GetTimestamp();
        var descriptions = new List<string>();

        for (var level = 0; (level < m_levels.Length); level++) {
            var low = m_levels[level].Low;
            var dims = m_layout.DimsOf(level: level);
            var ages = new List<float>();
            var visible = new List<float>();
            var outside = new List<float>();

            for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                    for (var x = low.X; (x < (low.X + dims.X)); x++) {
                        if (!m_levels[level].Cache.TryGetValue(key: (x, y, z), value: out var probe)) { continue; }
                        var next = Math.Clamp(((probe.Lighting + 1) - m_versionBase), 0, (m_versionTimes.Count - 1));
                        var age = ((probe.Lighting == m_lightingVersion) ? 0 : (float)Stopwatch.GetElapsedTime(m_versionTimes[next], now).TotalSeconds);

                        ages.Add(item: age);
                        var center = (new Vector3(x: (x + .5f), y: (y + .5f), z: (z + .5f)) * m_layout.SpacingOf(level: level));

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

        static float Median(List<float> values) => ((values.Count == 0) ? 0 : values[(values.Count / 2)]);
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
    // Whether a frame whose bake still lands opaque static meshes builds a scene from what has landed: never a lighting-converged
    // frame, which renders the whole bake; otherwise once no build runs, the installed scene's trace has finished (or no
    // scene is installed), and PendingSceneInterval has passed since the last build began. The finished trace paces the
    // rebuilds, so the probes always finish measuring one landed set before the next replaces it.
    private bool ScenePendingDue(SdfFrame frame) =>
        (!frame.ConvergesLighting && (m_build is null or { IsCompleted: true }) && ((m_scene is null) || !m_traceOwed) &&
        (!m_sceneClock.IsRunning || (m_sceneClock.Elapsed >= PendingSceneInterval)));
    // Rebuilds the scene on the thread pool when the static draws changed, from the scene it replaces so only geometry new
    // to it builds; the old scene traces until the new one lands. While the bake still owes opaque static meshes, a frame
    // with none landed builds nothing: an empty scene would measure open sky where the walls have yet to land.
    // Returns the build to wait for before this frame can use its scene, or null when it is already installed.
    private Task? RefreshScene(SdfFrame frame, bool bakePending = false) {
        var (key, draws) = StaticKey(frame: frame);

        // Even a frame matching the installed scene waits for an older build: that build could otherwise replace
        // the scene while this frame traces it. The lockstep caller resolves this frame again after it lands.
        if (m_build is { IsCompleted: false }) { return m_build; }
        if (key == m_sceneKey) { return null; }
        if (key == m_buildingKey) { return m_build; }
        if (bakePending && (draws == 0)) { return null; }

        return StartSceneBuild(draws: draws, frame: frame, key: key);
    }
    // The key of the frame's static draws, in order, and their count. The key folds each static draw's own hash (its mesh,
    // matrix and material), and it follows the producer's draw journal: when the list is the one last folded and the
    // journal covers every change since, only the rows it names are hashed again, and a key whose rows all hash as they
    // did, static as they were, is the key it was. Any other frame folds the whole list.
    private (int Key, int Draws) StaticKey(SdfFrame frame) {
        var meshDraws = frame.MeshDraws;
        var journal = frame.MeshDrawChanges;

        if (
            ReferenceEquals(objA: meshDraws, objB: m_keyList) &&
            (journal is not null) &&
            ReferenceEquals(objA: journal, objB: m_keyJournal) &&
            (meshDraws.Count == m_drawKeys.Length) &&
            !((frame.MeshDrawsRevision != m_keyRevision) && (frame.MeshDrawChangesSerial <= m_keySerial)) &&
            journal.TryCollect(since: m_keySerial, through: frame.MeshDrawChangesSerial, into: m_keyRuns) &&
            JournalKeepsKey(meshDraws: meshDraws)
        ) {
            m_keySerial = frame.MeshDrawChangesSerial;
            m_keyRevision = frame.MeshDrawsRevision;

            return (m_keyValue, m_keyStatic);
        }

        var key = 17;
        var draws = 0;

        if (m_drawKeys.Length != meshDraws.Count) {
            m_drawKeys = new int[meshDraws.Count];
            m_drawStatic = new bool[meshDraws.Count];
        }
        for (var index = 0; (index < meshDraws.Count); index++) {
            var draw = meshDraws[index];
            var isStatic = SdfMeshIrradianceScene.IsStatic(draw: in draw);

            m_drawStatic[index] = isStatic;
            if (!isStatic) { continue; }
            m_drawKeys[index] = DrawKey(draw: in draw);
            key = HashCode.Combine(value1: key, value2: m_drawKeys[index]);
            draws++;
        }

        m_keyList = meshDraws;
        m_keyJournal = journal;
        m_keySerial = frame.MeshDrawChangesSerial;
        m_keyRevision = frame.MeshDrawsRevision;
        m_keyValue = key;
        m_keyStatic = draws;

        return (key, draws);
    }
    // Whether every row the collected journal names leaves the static key as it was: not static now nor at the last
    // fold, or static both times with the hash it had.
    private bool JournalKeepsKey(IReadOnlyList<SdfMeshDraw> meshDraws) {
        for (var run = 0; (run < m_keyRuns.Count); run++) {
            var end = Math.Min(val1: meshDraws.Count, val2: (m_keyRuns.Start(index: run) + m_keyRuns.Length(index: run)));

            for (var index = m_keyRuns.Start(index: run); (index < end); index++) {
                var draw = meshDraws[index];
                var isStatic = SdfMeshIrradianceScene.IsStatic(draw: in draw);

                if ((isStatic != m_drawStatic[index]) || (isStatic && (DrawKey(draw: in draw) != m_drawKeys[index]))) {
                    return false;
                }
            }
        }

        return true;
    }
    private static int DrawKey(in SdfMeshDraw draw) =>
        HashCode.Combine(value1: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: draw.Mesh), value2: draw.ObjectToWorld, value3: draw.Material);
    // Apart from RefreshScene, since the build's closure is made on entry to the method holding it, and nearly every frame
    // matches the installed scene.
    private Task StartSceneBuild(SdfFrame frame, int key, int draws) {
        if (!m_trimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested += TrimRetained; m_trimListening = true; }
        var snapshot = frame.MeshDraws.ToArray();
        var previous = m_scene?.Acquire();
        var previousDraws = m_diagnosticDraws;
        var hadScene = (m_scene is not null);
        var snapshotClaims = new SdfMeshClaims(meshes: snapshot.Select(selector: static draw => draw.Mesh));

        m_buildingKey = key;
        m_sceneClock.Restart();
        m_build = Task.Run(action: () => {
            using var snapshotBorrow = snapshotClaims;
            using var previousBorrow = previous;
            var clock = Stopwatch.StartNew();
            var scene = SdfMeshIrradianceScene.Build(draws: snapshot, previous: previous, counters: Counters);
            var changed = (hadScene ? ChangedBoxes(after: snapshot, before: previousDraws) : null);

            Counters.Count(kind: Scenes);
            Counters.Add(amount: scene.GeometriesBuilt, kind: GeometriesBuilt);
            Counters.Add(amount: scene.GeometriesReused, kind: GeometriesReused);
            Counters.Add(amount: scene.TrianglesBuilt, kind: TrianglesBuilt);
            lock (m_gate) {
                if (m_disposed) { scene.Dispose(); return; }
                var oldScene = m_scene;

                m_scene = scene;
                oldScene?.Dispose();
                m_diagnosticDraws = snapshot.Where(predicate: d => SdfMeshIrradianceScene.IsStatic(draw: d)).ToArray();
                m_sceneKey = key;
                m_buildingKey = 0;
                AdvanceGeometry(changed: changed);
            }
            Console.Error.WriteLine(value: $"[sdf.mesh-gi] scene draws={draws} triangles={(scene?.TriangleCount ?? 0)} geometries={(scene?.GeometryCount ?? 0)} built={(scene?.GeometriesBuilt ?? 0)} built-triangles={(scene?.TrianglesBuilt ?? 0)} reused={(scene?.GeometriesReused ?? 0)} retained-triangles={(scene?.TrianglesRetained ?? 0)} seconds={clock.Elapsed.TotalSeconds:0.000}");
        });
        return m_build;
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
        var localKey = HashCode.Combine(value1: frame.IndirectGains, value2: frame.IndirectSources, value3: MaterialsKey(program: frame.Program));
        var sameSky = true;
        var skyBlock = default(SdfSkyBlock);
        var records = frame.Lights.Records;

        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = records[index];

            if (light.IsTraced && (light.Kind != SdfLightKind.Directional)) { localKey = HashCode.Combine(value1: localKey, value2: index, value3: light); }
        }

        var key = HashCode.Combine(value1: localKey, value2: layout.Bounce, value3: grid, value4: frame.ConvergesLighting);

        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = captureLights[index];

            if (light.Kind == SdfLightKind.Directional) { key = HashCode.Combine(value1: key, value2: index, value3: light); }
        }
        if ((frame.IndirectSources & SdfIndirectSources.Sky) != 0) {
            frame.Sky.Pack(captureLights, frame.FarDistance, m_skyDetails, out skyBlock, m_skyLayers);
            sameSky = SdfSkyEnvironment.SameMap(block: skyBlock, layers: m_skyLayers, otherBlock: m_lightingSky, otherLayers: m_lightingSkyLayers);
        }
        if ((key == m_lightingKey) && sameSky && (m_lighting is not null)) { return; }
        if (!frame.ConvergesLighting && (m_lighting is not null) && (Stopwatch.GetElapsedTime(startingTimestamp: m_lightingCheckedAt) < LightingInterval)) { return; }

        var skyOnly = ((key == m_lightingKey) && (m_lighting is not null));

        if ((localKey != m_localKey) || (m_lighting is null)) {
            m_localKey = localKey;
            m_localVersion++;
        }

        var captured = Lighting.Capture(captureLights: captureLights, frame: frame, localVersion: m_localVersion, previous: m_lighting, scratch: m_captureScratch, version: (m_lightingVersion + 1));

        if ((frame.IndirectSources & SdfIndirectSources.Sky) != 0) { Counters.Count(kind: SkyCaptures); }
        // Exposure reads the current capture's bound even when its irradiance stays in the same grid cell.
        m_skyPeak = captured.SkyPeak;

        m_lightingCheckedAt = Stopwatch.GetTimestamp();
        m_lightingKey = key;
        m_lightingSky = skyBlock;
        m_skyLayers.CopyTo(array: m_lightingSkyLayers, index: 0);
        if (skyOnly && captured.SkyMatches(other: m_lighting!, exact: (frame.ConvergesLighting || grid.Enabled))) { return; }
        m_lightingVersion++;
        m_lighting = captured;
        m_versionTimes.Add(item: captured.CapturedAt);
        if (m_versionTimes.Count > MaxVersionTimes) {
            m_versionTimes.RemoveRange(count: (MaxVersionTimes / 2), index: 0);
            m_versionBase += (MaxVersionTimes / 2);
        }
        Owe();
    }
    // The part of a program's materials the relight reads (Shade: what each reflects and emits), hashed once per program
    // and scene: the materials the static scene's triangles carry (SdfMeshIrradianceScene.Materials), by id. A program
    // rebuilt with one of them changed in place (a bound color's move, a placement showing another creation over the same
    // meshes) leaves the static draws as they were, so only this key relights it; a body's palette coming or going, which
    // no probe ray meets, leaves it alone.
    private int MaterialsKey(SdfProgram program) {
        if (ReferenceEquals(objA: program, objB: m_materialsProgram) && ReferenceEquals(objA: m_scene, objB: m_materialsScene)) { return m_materialsKey; }

        var key = 17;
        var materials = program.Appearance.Materials;

        foreach (var id in (m_scene?.Materials ?? [])) {
            if ((id < 0) || (id >= materials.Count)) {
                key = HashCode.Combine(value1: key, value2: id);
                continue;
            }

            var material = materials[id];

            key = HashCode.Combine(value1: key, value2: id, value3: material.Albedo, value4: material.Emissive, value5: material.Metal, value6: material.Bleed, value7: material.EmissiveLane);
        }
        m_materialsProgram = program;
        m_materialsScene = m_scene;
        m_materialsKey = key;

        return key;
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
    private (int X, int Y, int Z)[] Lows() {
        var lows = new (int X, int Y, int Z)[m_levels.Length];

        for (var level = 0; (level < m_levels.Length); level++) { lows[level] = m_levels[level].Low; }
        return lows;
    }
    // Owes the refill of the windows the volume just moved: no level publishes until it lands, and it runs on the thread
    // pool (Refill), or in a lighting-converged frame on the frame's thread, since a window that jumps far re-stores every slot.
    private void OweRefill(bool pooled) {
        m_windowVersion++;
        m_refillOwed = true;
        foreach (var level in m_levels) { level.Unwritten = m_layout.CountOf(level: level.Index); }
        if (pooled && !m_refillRunning) {
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

            if (m_refillWords.Length < (cells * SdfMeshIrradianceProbe.ProbeWords)) {
                m_refillWords = new uint[(cells * SdfMeshIrradianceProbe.ProbeWords)];
            }

            var composed = 0;

            for (var index = 0; (index < cells); index++) {
                var entry = m_refillCells[index];

                if (Compose(cell: entry.Cell, level: entry.Level, words: m_refillWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (composed * SdfMeshIrradianceProbe.ProbeWords)))) {
                    m_refillCells[composed++] = entry;
                }
            }

            lock (m_gate) {
                if (version != m_windowVersion) { continue; }

                for (var index = 0; (index < composed); index++) {
                    var (level, cell) = m_refillCells[index];

                    if (Holds(cell: cell, level: level)) { continue; }
                    m_refillWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords))
                        .CopyTo(destination: SlotWords(cell: cell, level: level));
                    m_levels[level].Unwritten--;
                }

                m_refillOwed = false;
                m_refillRunning = false;

                return;
            }
        }
    }
    // The slot a level's cell takes in the volume: its level's first slot plus its slot in the level's window.
    private int VolumeSlot(int level, (int X, int Y, int Z) cell) =>
        (m_layout.OffsetOf(level: level) + SlotOf(dims: m_layout.DimsOf(level: level), x: cell.X, y: cell.Y, z: cell.Z));
    // Whether a level's cell's slot holds that cell's probe.
    private bool Holds(int level, (int X, int Y, int Z) cell) =>
        ((m_volume[(VolumeSlot(cell: cell, level: level) * SdfMeshIrradianceProbe.ProbeWords)] & ~SdfMeshIrradianceProbe.InvalidFlag) == (KeyOf(x: cell.X, y: cell.Y, z: cell.Z) | SdfMeshIrradianceProbe.WrittenFlag));
    // A level's cell's slot words in the volume, marked changed for the level's next publication.
    private Span<uint> SlotWords(int level, (int X, int Y, int Z) cell, bool gpuWritten = false) {
        var slot = VolumeSlot(cell: cell, level: level);

        if ((GpuTrace is not null) && !gpuWritten) { _ = m_gpuDirty.Add(index: slot); }

        _ = m_levels[level].Changed.Add(index: slot);
        return m_volume.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (slot * SdfMeshIrradianceProbe.ProbeWords));
    }
    // Composes a level's cached cell's slot words from its own sky shares, relit cubes, surface map and light visibility;
    // false, writing nothing, when the cache holds no probe of it. It reads the cache alone, so it runs outside the gate.
    private bool Compose(int level, (int X, int Y, int Z) cell, Span<uint> words) {
        if (!m_levels[level].Cache.TryGetValue(key: cell, value: out var probe)) { return false; }
        Compose(cell: cell, probe: in probe, words: words);
        return true;
    }
    private static void Compose(in ProbeData probe, (int X, int Y, int Z) cell, Span<uint> words) {

        var sky = probe.Sky;
        var radiance = probe.Radiance;
        var skyLight = probe.SkyLight;

        words[0] = (KeyOf(x: cell.X, y: cell.Y, z: cell.Z) | SdfMeshIrradianceProbe.WrittenFlag) | (probe.Invalid ? SdfMeshIrradianceProbe.InvalidFlag : 0u);
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

    /// <summary>Returns the key a cell's probe carries: each coordinate's low ten bits. KEEP IN SYNC with
    /// <c>sdfMeshIrradianceKey</c>.</summary>
    /// <param name="x">The cell's first coordinate.</param>
    /// <param name="y">The cell's second coordinate.</param>
    /// <param name="z">The cell's third coordinate.</param>
    /// <returns>The key, without its flags.</returns>
    public static uint KeyOf(int x, int y, int z) => (((uint)x) & 1023u) | ((((uint)y) & 1023u) << 10) | ((((uint)z) & 1023u) << 20);
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
        (int Geometry, Box[]? Boxes)[] Changes, long[] VersionTimes, int VersionBase, int Sweep, float AgeTarget, CameraSnapshot[] ViewCameras);

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
    // The background worker. Stable lighting takes whole-window bounce sweeps; changing lighting interleaves coarse
    // phases, with overdue probes forced. Work takes trace demand first, then relative age, view/change weight and
    // spatial bands, each probe reading the volume about its hits as it stands; another sweep starts while any
    // probe of it moved past the convergence bound or was relit under a lighting since replaced, so a change anywhere in
    // what a probe reads wakes it through the next sweep, and the iteration stops only when a whole sweep under
    // unchanged inputs moved nothing (or after MaxSweeps). The worker works through its list a batch at a time while the
    // frames' budget grants it, taking the latest lighting for each batch; it returns once the budget is spent (a probe
    // whose trace the budget cannot fund waits for the next grant), when a lighting-converged frame asks it to yield, or once
    // nothing is left to do.
    private void Work() {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = BackgroundWorkers };

        while (true) {
            lock (m_gate) {
                if (m_yield) { return; }
            }
            if (TakeSnapshot() is not { } snapshot) { return; }
            using var sceneBorrow = snapshot.Scene;
            var candidates = m_candidates;

            // A list the budget interrupted resumes where it stopped while its geometry, windows and sweep stand:
            // listing and ordering a window's probes is itself work a frame's grant should not repeat.
            if (!m_listed || (m_listGeometry != snapshot.Geometry) || (m_listWindow != snapshot.WindowVersion) || (m_listLayout != snapshot.Layout) ||
                (m_listSweep != snapshot.Sweep) || (m_listCursor >= candidates.Count)) {
                var now = Stopwatch.GetTimestamp();
                var stale = List(ages: m_ages, all: false, candidates: candidates, snapshot: snapshot);

                lock (m_gate) {
                    if (!Stands(snapshot: in snapshot) || (snapshot.Sweep != m_sweep)) { continue; }
                    Measure(now: now);
                    if (candidates.Count == 0) {
                        m_listed = false;
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
                m_listCursor = 0;
            }

            var index = m_listCursor;
            var starved = false;

            while (index < candidates.Count) {
                Lighting lighting;
                int take;

                lock (m_gate) {
                    if (m_disposed || !Stands(snapshot: in snapshot)) { break; }
                    if (m_yield) {
                        m_listed = false;
                        return;
                    }
                    lighting = m_lighting!;

                    var trace = candidates[index].Trace;
                    var credits = (trace ? ((m_budget.Trace > 0) ? m_traceCredits : long.MaxValue) : ((m_budget.Relight > 0) ? m_relightCredits : long.MaxValue));

                    if (credits <= 0) {
                        if (!trace) {
                            m_listCursor = index;
                            if (starved) { m_listed = false; }
                            return;
                        }
                        starved = true;
                        while ((index < candidates.Count) && candidates[index].Trace) { index++; }
                        continue;
                    }
                    take = 0;
                    while (((index + take) < candidates.Count) && (take < BatchProbes) && (take < credits) && (candidates[(index + take)].Trace == trace)) { take++; }
                    if (trace) {
                        if (m_budget.Trace > 0) { m_traceCredits -= take; }
                    } else if (m_budget.Relight > 0) {
                        m_relightCredits -= take;
                    }
                }
                Batch(candidates: candidates, count: take, first: index, lighting: lighting, parallel: parallel, pass: -1, snapshot: in snapshot);
                index += take;
            }
            m_listed = false;
            // Probes whose traces the budget could not fund wait for the next grant; listing again now would find them
            // unfunded and spin.
            if (starved) { return; }
        }
    }
    // A lighting-converged frame's work: every probe of the windows traced against the current geometry where it is not already,
    // then the whole window relit from no bounce, each later pass relighting every probe from the bounce the one before
    // left, until a whole pass moves no probe past the convergence bound (or MaxSweeps passes), so the light it renders
    // is a function of its scene, lighting and windows alone and never of the bounce earlier frames left in the cache.
    private void Drain() {
        if (TakeSnapshot() is not { } snapshot) { return; }
        using var sceneBorrow = snapshot.Scene;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = DrainWorkers };
        var candidates = m_candidates;

        m_listed = false;

        _ = List(ages: null, all: true, candidates: candidates, snapshot: snapshot);
        for (var pass = 0; (pass < MaxSweeps); pass++) {
            bool moved;

            lock (m_gate) { m_sweepMoved = false; }
            for (var index = 0; (index < candidates.Count); index += DrainBatchProbes) {
                Batch(candidates: candidates, count: Math.Min(val1: DrainBatchProbes, val2: (candidates.Count - index)), first: index, lighting: snapshot.Lighting, parallel: parallel, pass: pass, snapshot: in snapshot);
            }
            // Every probe holds a trace now; later passes relight them all from the bounce this one left.
            for (var index = 0; (index < candidates.Count); index++) { candidates[index] = (candidates[index] with { Trace = false }); }
            lock (m_gate) { moved = m_sweepMoved; }
            if (!(snapshot.Lighting.Feedback && snapshot.Layout.Bounce) || ((pass > 0) && !moved)) { break; }
        }
        lock (m_gate) {
            m_sweepMoved = false;
            if (!Stands(snapshot: in snapshot) || (snapshot.Lighting.Version != m_lightingVersion)) { return; }
            m_drained = DrainKey();
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
    // Lists eligible probes, every one for a drain. The shared order puts trace demand first, then relight age relative
    // to its bound with view/change weights, level, capture age and Morton bands. Returns whether lighting or a
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
        }
        var interleave = m_interleaving;

        if (ages is not null) {
            ages.Clear();
            Array.Clear(array: m_listAgeMax);
            Array.Clear(array: m_listAgeCount);
        }
        if (GpuTrace is not null) { return ListGpu(ages: ages, all: all, candidates: candidates, interleave: interleave, snapshot: in snapshot); }
        var stale = false;
        var layout = snapshot.Layout;
        var lighting = snapshot.Lighting;
        var now = Stopwatch.GetTimestamp();

        candidates.Clear();
        for (var level = 0; (level < m_levels.Length); level++) {
            var cache = m_levels[level].Cache;
            var low = snapshot.Lows[level];
            var dims = layout.DimsOf(level: level);
            var spacing = layout.SpacingOf(level: level);

            for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                    for (var x = low.X; (x < (low.X + dims.X)); x++) {
                        var present = cache.TryGetValue(key: (x, y, z), value: out var probe);
                        var trace = (!present || ((probe.Geometry != snapshot.Geometry) && Affected(cell: (x, y, z), layout: layout, probe: in probe, snapshot: in snapshot, spacing: spacing)));
                        var rebase = (present && !trace && (probe.Geometry != snapshot.Geometry));
                        var current = (present && (probe.Lighting == lighting.Version));
                        var center = (new Vector3(x: (x + 0.5f), y: (y + 0.5f), z: (z + 0.5f)) * spacing);
                        var priority = ProbePriority((x, y, z), low, center, snapshot.Camera, spacing);
                        var age = ((!present || current) ? 0 : (float)Stopwatch.GetElapsedTime(StaleSince(lighting: probe.Lighting, snapshot: in snapshot), now).TotalSeconds);
                        var bound = SdfMeshProbeRelight.AgeBoundOf(level: level, target: snapshot.AgeTarget);

                        if (present) {
                            RecordAge(age: age, ages: ages, level: level);
                        }
                        if (!all && !trace && !rebase && (probe.Sweep == snapshot.Sweep) && !((bound > 0) && (age >= bound))) {
                            stale |= !current;
                            continue;
                        }
                        if (interleave && !trace && !rebase && !SdfMeshProbeRelight.RelightDue(m_selectionSweep, ProbePhase(x: x, y: y, z: z), SdfMeshProbeRelight.InterleaveOf(level: level), age, bound)) {
                            stale = true;
                            continue;
                        }
                        candidates.Add(item: new Candidate(Cell: (x, y, z), Band: priority.Band, Morton: priority.Morton, Level: level, LitAt: (present ? probe.LitAt : 0L), Trace: trace,
                            Overdue: ((bound > 0) && (age >= bound)), Urgency: AgePriority(age, bound, InAnyView(center: center, snapshot: in snapshot), probe.Moved)));
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
        candidates.Sort(comparison: static (a, b) =>
            ((a.Trace != b.Trace) ? (a.Trace ? -1 : 1) :
            (a.Trace ? ((a.Level != b.Level) ? a.Level.CompareTo(value: b.Level) : CompareSpatial(a: a, b: b)) :
            ((a.Overdue != b.Overdue) ? (a.Overdue ? -1 : 1) :
            ((a.Urgency != b.Urgency) ? b.Urgency.CompareTo(value: a.Urgency) :
            ((a.Level != b.Level) ? a.Level.CompareTo(value: b.Level) :
            ((a.LitAt != b.LitAt) ? a.LitAt.CompareTo(value: b.LitAt) : CompareSpatial(a: a, b: b))))))));
    }
    private static int AgePriority(float age, float bound, bool inView, bool changed) {
        var relative = ((bound > 0) ? (age / bound) : age);
        // Overdue probes ignore view/change weights. Below the bound these weights only reorder work; none retires.
        var weight = (((bound > 0) && (age >= bound)) ? 1 : ((inView ? 1 : .25f) * (changed ? 1.25f : 1)));

        return ((int)Math.Clamp(Math.Floor(d: ((((double)relative) * weight) * 8)), 0, int.MaxValue));
    }
    private void RecordAge(List<float>? ages, int level, float age) {
        if (ages is null) { return; }
        ages.Add(item: age);
        m_listAgeMax[level] = MathF.Max(x: m_listAgeMax[level], y: age);
        m_listAgeCount[level]++;
    }
    private static int ProbePhase(int x, int y, int z) => (x & 1) | ((y & 1) << 1) | ((z & 1) << 2);
    private static int CompareSpatial(Candidate a, Candidate b) => ((a.Band != b.Band) ? a.Band.CompareTo(value: b.Band) : a.Morton.CompareTo(value: b.Morton));
    // Eight-cell radial bands retain near-first demand while adjacent probes share hierarchy cache lines.
    // Relative window coordinates avoid signed-cell wrap at zero; the existing Maths interleave supplies Morton order.
    private static (int Band, uint Morton) ProbePriority((int X, int Y, int Z) cell, (int X, int Y, int Z) low, Vector3 center, Vector3 camera, Vector3 spacing) =>
        (((int)(((center - camera) / spacing).Length() / 8)), BinaryIntegerFunctions.BitwiseTriple<uint, uint>(second: ((uint)(cell.Y - low.Y)), third: ((uint)(cell.Z - low.Z)), value: ((uint)(cell.X - low.X))));
    // When a probe's lighting went stale: the capture of the version after its own, or the oldest capture remembered.
    private static long StaleSince(int lighting, in Snapshot snapshot) {
        var index = ((lighting + 1) - snapshot.VersionBase);

        return ((snapshot.VersionTimes.Length == 0) ? Stopwatch.GetTimestamp() : snapshot.VersionTimes[Math.Clamp(value: index, min: 0, max: (snapshot.VersionTimes.Length - 1))]);
    }
    // Records the lighting age a listing found and the relight rate of the cycle it closes, and reports a cycle whose
    // oldest lighting is past the budget's age target; under the gate.
    private void Measure(long now) {
        var ages = m_ages;

        if (ages.Count > 0) {
            ages.Sort();
            m_ageMedian = ages[(ages.Count / 2)];
            m_ageMax = ages[^1];
        } else {
            m_ageMedian = m_ageMax = 0f;
        }

        var seconds = ((m_cycleStart == 0L) ? 0d : Stopwatch.GetElapsedTime(endingTimestamp: now, startingTimestamp: m_cycleStart).TotalSeconds);

        if (seconds > 0d) { m_cycleRate = ((float)((Counters.Read(kind: Relit) - m_cycleRelit) / seconds)); }
        m_cycleStart = now;
        m_cycleRelit = Counters.Read(kind: Relit);
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
        Counters.Count(kind: Behind);
        if ((m_behindAt != 0L) && (Stopwatch.GetElapsedTime(endingTimestamp: now, startingTimestamp: m_behindAt) < BehindInterval)) { return; }
        m_behindAt = now;

        Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"[sdf.mesh-gi] behind level={worst} age-max={m_listAgeMax[worst]:0.##} target={SdfMeshProbeRelight.AgeBoundOf(level: worst, target: m_budget.Age):0.##} combined-age-p50={m_ageMedian:0.##} probes={ages.Count} relit-per-second={m_cycleRate:0} needed-per-second={needed:0} shortfall-per-second={MathF.Max(x: 0f, y: (needed - m_cycleRate)):0} budget-relight={m_budget.Relight}"));
    }
    // Traces and relights one batch of the listed probes on the pool, then lands it. The batch reads the cache as it stood
    // before the batch, so its probes' bounce does not depend on the order the pool runs them in. A pass of zero or more
    // is a drain's: zero relights with no bounce. A batch landing after its geometry or windows moved is dropped.
    private void Batch(List<Candidate> candidates, int first, int count, Lighting lighting, ParallelOptions parallel, int pass, in Snapshot snapshot) {
        var results = m_results;

        if (results.Length < count) {
            results = new ProbeData[Math.Max(val1: BatchProbes, val2: count)];
            m_results = results;
        }

        var traces = false;

        for (var index = 0; (index < count); index++) { traces |= candidates[(first + index)].Trace; }

        // A whole batch's hit block even for a short batch, so every block is large enough for the large object heap. On
        // the GPU the hit words only pass through the batch (ReleaseHits), so the block borrows the worker's scratch
        // array rather than holding one of its own.
        var blockProbes = Math.Max(val1: BatchProbes, val2: count);
        var borrowed = (traces && (GpuTrace is not null));

        if (borrowed) { GrowScratch(array: ref m_gpuHits, length: (blockProbes * SdfMeshProbeDepth.HitWords)); }
        var hits = (traces ? new ProbeBlock(count: blockProbes, hits: (borrowed ? m_gpuHits : null)) : null);
        var lights = new LightBlock(count: count);
        var feedback = (lighting.Feedback && snapshot.Layout.Bounce && (pass != 0));
        var layout = snapshot.Layout;
        var lows = snapshot.Lows;
        var scene = snapshot.Scene;
        var geometry = snapshot.Geometry;
        var sweep = snapshot.Sweep;

        GrowScratch(array: ref m_gpuFresh, length: count);
        var fresh = m_gpuFresh;
        var prepared = (traces ? PlaceBatch(block: hits!, candidates: candidates, count: count, first: first, fresh: fresh, geometry: geometry, layout: layout, lows: lows, parallel: parallel, scene: scene) : null);
        var committed = true;

        try {
            _ = Parallel.For(fromInclusive: 0, toExclusive: count, parallelOptions: parallel, body: index => {
                var candidate = candidates[(first + index)];

                results[index] = Process(candidate: candidate, feedback: feedback, geometry: geometry, hits: hits, index: index, layout: layout, lighting: lighting,
                    lights: lights, lows: lows, prepared: prepared?[index], scene: scene, sweep: sweep);
            });
            if (GpuTrace is not null) { committed = RelightGpu(candidates: candidates, count: count, feedback: feedback, first: first, lighting: lighting, lights: lights, probes: results, snapshot: in snapshot); }
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

                if (result.Hits is null) { continue; }
                relit++;
                if (pass < 0) { m_drained = default; }
                if (candidate.Trace) { traced++; }
                moved |= result.Moved;
                m_levels[candidate.Level].Cache[candidate.Cell] = result;
                m_landed.Add(item: (candidate.Level, candidate.Cell));
            }
            m_traced += traced;
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

            if (m_restoreWords.Length < (cells * SdfMeshIrradianceProbe.ProbeWords)) {
                m_restoreWords = new uint[(cells * SdfMeshIrradianceProbe.ProbeWords)];
            }

            var composed = 0;

            for (var index = 0; (index < cells); index++) {
                var entry = m_restoreCells[index];

                if (Compose(cell: entry.Cell, level: entry.Level, words: m_restoreWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (composed * SdfMeshIrradianceProbe.ProbeWords)))) {
                    m_restoreCells[composed++] = entry;
                }
            }

            lock (m_gate) {
                if (version != m_windowVersion) { continue; }

                for (var index = 0; (index < composed); index++) {
                    var (level, cell) = m_restoreCells[index];

                    if (!Holds(cell: cell, level: level)) { m_levels[level].Unwritten--; }
                    m_restoreWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords)).CopyTo(destination: SlotWords(cell: cell, level: level, gpuWritten: (GpuTrace is not null)));
                }
                m_landed.Clear();

                return;
            }
        }
    }
    // One probe of a batch: its placement and hit buffer, when it lacks a trace against the current geometry, then its
    // relight under the batch's lighting. An invalid probe takes no relight: no view reads it.
    private ProbeData Process(in Candidate candidate, int geometry, Layout layout, Lighting lighting, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene,
        bool feedback, ProbeBlock? hits, LightBlock lights, int index, int sweep, ProbeData? prepared) {
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
        } else if (!cache.TryGetValue(key: candidate.Cell, value: out probe)) {
            // Only a drain whose first pass was dropped can list a relight of an untraced probe; nothing lands.
            return default;
        }

        var passes = ((probe.Lighting == lighting.Version) ? (probe.Passes + 1) : 1);
        var previous = probe;

        // A listed probe either traced against this geometry or holds hits no change since its own geometry reaches.
        probe = (probe with { Geometry = geometry, LightIndex = index, Moved = false, Sweep = sweep, Lighting = lighting.Version, LocalVersion = lighting.LocalVersion, LitAt = lighting.CapturedAt, Passes = passes, Shading = lights });
        if (!probe.Invalid && (GpuTrace is null)) {
            var origin = ((new Vector3(x: (candidate.Cell.X + 0.5f), y: (candidate.Cell.Y + 0.5f), z: (candidate.Cell.Z + 0.5f)) * layout.SpacingOf(level: candidate.Level)) + probe.Offset);

            // A rebased probe keeps its hits, but the shadow rays from them toward the point lights may cross the change.
            var reuseLocal = (!candidate.Trace && (previous.Shading is not null) && (previous.Geometry == geometry) && (previous.LocalVersion == lighting.LocalVersion));

            if (reuseLocal) {
                previous.Local.CopyTo(destination: lights.Local(index: index));
                previous.Lights.CopyTo(destination: lights.Lights(index: index));
            }
            Relight(feedback: feedback, hits: probe.HitWords, layout: layout, lighting: lighting, lights: lights.Lights(index: index), local: lights.Local(index: index),
                lows: lows, origin: origin, radiance: lights.Radiance(index: index), reuseLocal: reuseLocal, scene: scene, sky: lights.SkyLight(index: index));
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
    // radiance about it into `sky`. The same quadrature weighs the cubes as weighed its sky shares.
    private void Relight(Vector3 origin, ReadOnlySpan<uint> hits, Layout layout, Lighting lighting, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene,
        bool feedback, Span<Vector3> radiance, Span<Vector3> local, Span<Vector3> sky, bool reuseLocal, Span<uint> lights) {
        var directions = SdfMeshProbeDepth.RayDirections;
        var weights = SdfMeshProbeDepth.RayWeights;

        radiance.Clear();
        sky.Clear();
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

            var (varying, fixedLight) = Shade(feedback: feedback, layout: layout, lighting: lighting, local: !reuseLocal, lows: lows, material: material, normal: normal,
                point: (origin + (direction * distance)), scene: scene);

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
            // A light past its range lights nothing here and stays unseen; one at the probe's origin is seen.
            foreach (var point in lighting.Points) {
                var toward = (point.Position - origin);
                var length = toward.Length();

                if ((length >= point.Range) || ((length > SdfMeshIrradianceProbe.LightBias) && !scene.Unblocked(direction: (toward / length), maximum: (length - SdfMeshIrradianceProbe.LightBias), origin: origin))) { continue; }
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
    // falloff, cone and a traced shadow, and its emission.
    private (Vector3 Varying, Vector3 Local) Shade(Lighting lighting, Vector3 point, Vector3 normal, int material, bool feedback, bool local, Layout layout, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene) {
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
                    if ((distance > SdfMeshIrradianceProbe.LightBias) && !scene.Unblocked(direction: direction, maximum: (distance - SdfMeshIrradianceProbe.LightBias), origin: lifted)) { continue; }
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
        if (feedback && (reflected != Vector3.Zero)) {
            varying += ((reflected * Incoming(layout: layout, lows: lows, normal: normal, point: point)) * lighting.Gains.Feedback);
        }

        return (varying, fixedLight);
    }
    // The light a surface receives from the cached probes about it, as the views pass reads the volume
    // (sdfMeshProbesAt, shade/sdf-mesh-irradiance.hlsli): the probes Select chooses, each weighted as Gather weighs it.
    // It reads each probe's own cubes, exactly as the published volume does.
    private Vector3 Incoming(Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z)[] lows) {
        var (sky, bounce) = IncomingParts(layout: layout, lows: lows, normal: normal, point: point);

        return (sky + bounce);
    }
    // Incoming's two terms apart: the sky light and the surfaces' radiance the probes' cubes hold.
    private (Vector3 Sky, Vector3 Bounce) IncomingParts(Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z)[] lows) {
        var squared = (normal * normal);
        Span<float> cornerWeights = stackalloc float[GatherSlots];
        Span<(int X, int Y, int Z)> cornerCells = stackalloc (int X, int Y, int Z)[GatherSlots];
        var cornerProbes = new Corners();

        _ = Select(cells: cornerCells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, layout: layout, lows: lows, normal: normal, point: point);

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
            return (Vector3.Zero, Vector3.Zero);
        }

        return ((sky / total), (bounce / total));
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

            selection = Select(cells: cells, cornerProbes: ref probes, cornerWeights: weights, layout: m_layout, lows: Lows(), normal: normal, point: point, reports: reports);

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
            var selection = Select(point: point, normal: normal, layout: m_layout, lows: Lows(),
                cells: cells, cornerWeights: weights, cornerProbes: ref probes);
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

            selection = Select(cells: cells, cornerProbes: ref probes, cornerWeights: weights, layout: layout, lows: Lows(), normal: normal, point: point, reports: reports);
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

            var (sky, bounce) = IncomingParts(layout: layout, lows: Lows(), normal: normal, point: point);

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
        Span<(int X, int Y, int Z)> cells, Span<float> cornerWeights, ref Corners cornerProbes, Span<SdfMeshProbeReport> reports = default) {
        var held = (m_levels.Length - 1);

        for (var level = 0; (level < held); level++) {
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

        for (var stage = 0; (stage <= (held + 2)); stage++) {
            var level = ((stage > (held + 1)) ? 0 : Math.Min(val1: stage, val2: held));
            var bounded = (stage <= held);

            if (((stage < held) || (stage > (held + 1))) && (held == 0)) { continue; }

            var reachShare = 1f;

            if (stage < held) {
                var beyond = Beyond(layout: layout, level: level, low: lows[level], normal: normal, point: point);
                var reach = SurfaceReachOf(spacing: layout.SpacingOf(level: level));

                if (beyond > reach) { continue; }
                reachShare = ReachShareOf(beyond: beyond, reach: reach);
            }
            if ((chosen >= 0) && (level == chosen)) { continue; }

            var slot = ((chosen >= 0) ? 8 : 0);
            var total = Gather(cells: cells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, depth: bounded, front: out var front, layout: layout, level: level,
                low: lows[level], normal: normal, point: point, reports: reports, slot: slot);

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
    private float Gather(int level, Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z) low, bool depth, int slot, Span<(int X, int Y, int Z)> cells, Span<float> cornerWeights, ref Corners cornerProbes, out float front, Span<SdfMeshProbeReport> reports = default) {
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
                (cell.Item3 < low.Z) || (cell.Item3 >= (low.Z + dims.Z)) ||
                !cache.TryGetValue(key: cell, value: out var probe)
            ) {
                continue;
            }

            var weights = new Vector3(
                x: ((offset.X > 0f) ? fraction.X : (1f - fraction.X)),
                y: ((offset.Y > 0f) ? fraction.Y : (1f - fraction.Y)),
                z: ((offset.Z > 0f) ? fraction.Z : (1f - fraction.Z))
            );
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
            var trilinear = ((weights.X * weights.Y) * weights.Z);
            var visibility = (probe.Invalid ? 0f : SdfMeshProbeDepth.Visibility(bounded: depth, surfaces: probe.Depth, toward: (pushed - probePoint)));
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
    private static float Cube(ReadOnlySpan<float> cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));
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
    // The index-th of count directions spread evenly over the sphere (a spherical Fibonacci lattice).
    private static Vector3 Fibonacci(int index, int count) {
        const float GoldenAngle = 2.39996323f;
        var z = (1f - (((2f * index) + 1f) / count));
        var radius = MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (z * z))));
        var angle = (index * GoldenAngle);

        return new Vector3(x: (radius * MathF.Cos(x: angle)), y: z, z: (radius * MathF.Sin(x: angle)));
    }

    // A static draw's mesh by reference, as the scene key counts it, with its transform and material.
    private sealed class DrawKeyComparer : IEqualityComparer<(SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material)> {
        public static DrawKeyComparer Instance { get; } = new();

        public bool Equals((SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material) x, (SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material) y) =>
            (ReferenceEquals(objA: x.Mesh, objB: y.Mesh) && (x.ObjectToWorld == y.ObjectToWorld) && (x.Material == y.Material));
        public int GetHashCode((SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material) obj) =>
            HashCode.Combine(value1: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: obj.Mesh), value2: obj.ObjectToWorld, value3: obj.Material);
    }
    // An axis-aligned world box.
    private readonly record struct Box(Vector3 Low, Vector3 High);
    // A probe the worker lists: its level and cell, whether it lacks a trace against the current geometry, the capture
    // time of the lighting it was last relit under (zero for none), the relights it holds under the current lighting,
    // and its radial priority band and Morton cell order within that band.
    private readonly record struct Candidate(int Level, (int X, int Y, int Z) Cell, bool Trace, long LitAt, int Band, uint Morton, bool Overdue, int Urgency);
    // One probe: its geometry (where it stands relative to its cell's centre, the share of its rays that met back faces,
    // whether it lies inside geometry, the geometry it was traced against, and its hit buffer, sky shares and surface map
    // in a trace batch's ProbeBlock), and its light (its radiance and sky-light cubes and traced-light visibility in a relight batch's
    // LightBlock, the lighting version and capture time it was relit under, and the relights it holds under that
    // version, the sweep it was last relit in and whether that relight moved it past the convergence bound). A batch
    // allocates one block of each rather than arrays a probe; a landed block is never written again, so
    // readers outside the gate read it whole.
    private readonly record struct ProbeData(int Geometry, ProbeBlock? Hits, int HitIndex, bool Invalid, Vector3 Offset, float Inside,
        LightBlock? Shading = null, int LightIndex = 0, int Lighting = 0, long LitAt = 0L, int Passes = 0, int Sweep = 0, bool Moved = false, int LocalVersion = 0, uint GpuSlot = 0, ulong GpuToken = 0) {
        public ReadOnlySpan<float> Sky => Hits!.Sky(index: HitIndex);
        public ReadOnlySpan<uint> HitWords => Hits!.Hits(index: HitIndex);
        public ReadOnlySpan<uint> Depth => Hits!.Depth(index: HitIndex);
        public ReadOnlySpan<Vector3> Radiance => Shading!.Radiance(index: LightIndex);
        public ReadOnlySpan<Vector3> Local => Shading!.Local(index: LightIndex);
        public ReadOnlySpan<uint> Lights => Shading!.Lights(index: LightIndex);
        public ReadOnlySpan<Vector3> SkyLight => Shading!.SkyLight(index: LightIndex);
    }

    // A block's words, zeroed, on the pinned heap. The probe cache's table, a large object that only a gen2 collects,
    // names the blocks for as long as their probes stand, so words allocated young would be marked through its cards and
    // copied into gen2 by the young collections the frame thread's allocation sets off; on the pinned heap a young
    // collection neither copies nor promotes them, and a gen2 reclaims a block no entry names.
    private static float[] BlockWords(int length) => GC.AllocateArray<float>(length: length, pinned: true);

    // The geometry words of one trace batch's probes, each probe's at its index: six sky shares, the packed octahedral
    // surface map and curvature, then the hit buffer, held as the floats' bits so a batch is one allocation.
    // `hits`, when given, is an array of at least count * HitWords words the block borrows until ReleaseHits; its words
    // are read only after the batch wrote them.
    private sealed class ProbeBlock(int count, uint[]? hits = null) {
        public const int FloatsPerProbe = (6 + SdfMeshIrradianceProbe.DepthWords);

        private readonly float[] m_floats = BlockWords(length: (count * FloatsPerProbe));
        private uint[]? m_hits = (hits ?? new uint[(count * SdfMeshProbeDepth.HitWords)]);

        public long Bytes => ((((long)m_floats.Length) * sizeof(float)) + ((m_hits?.LongLength ?? 0) * sizeof(uint)));

        public void ReleaseHits() => m_hits = null;
        public Span<float> Sky(int index) => m_floats.AsSpan(length: 6, start: (index * FloatsPerProbe));
        public Span<uint> Depth(int index) => MemoryMarshal.Cast<float, uint>(span: m_floats.AsSpan(length: SdfMeshIrradianceProbe.DepthWords, start: ((index * FloatsPerProbe) + 6)));
        public Span<uint> Hits(int index) => (m_hits ?? throw new InvalidOperationException(message: "Runtime hit words are resident on the GPU.")).AsSpan(length: SdfMeshProbeDepth.HitWords, start: (index * SdfMeshProbeDepth.HitWords));
    }
    // The light words of one relight batch's probes, each probe's at its index: the radiance cube (18 floats), the part of
    // it the point and spot lights and emission send (18 floats), its traced-light visibility words, then its sky-light
    // cube (18 floats).
    private sealed class LightBlock(int count) {
        public const int FloatsPerProbe = (54 + SdfMeshIrradianceProbe.LightWords);

        private readonly float[] m_floats = BlockWords(length: (count * FloatsPerProbe));

        public long Bytes => (((long)m_floats.Length) * sizeof(float));

        public Span<Vector3> Radiance(int index) => MemoryMarshal.Cast<float, Vector3>(span: m_floats.AsSpan(length: 18, start: (index * FloatsPerProbe)));
        public Span<Vector3> Local(int index) => MemoryMarshal.Cast<float, Vector3>(span: m_floats.AsSpan(length: 18, start: ((index * FloatsPerProbe) + 18)));
        public Span<uint> Lights(int index) => MemoryMarshal.Cast<float, uint>(span: m_floats.AsSpan(length: SdfMeshIrradianceProbe.LightWords, start: ((index * FloatsPerProbe) + 36)));
        public Span<Vector3> SkyLight(int index) => MemoryMarshal.Cast<float, Vector3>(span: m_floats.AsSpan(length: 18, start: (((index * FloatsPerProbe) + 36) + SdfMeshIrradianceProbe.LightWords)));
    }
    // The probes by cell: an open-addressed table of entries, so a probe the worker lands costs no allocation and the walk
    // into an untraced area promotes no small objects. The worker writes it under the volume's gate, one writer at a time;
    // the frame, the refill and the worker's threads read it without the gate, each entry under a sequence lock (its
    // version is odd while a write is in progress), and a grown table is published whole, so a reader holding the old one
    // reads a consistent, slightly earlier cache.
    private sealed class ProbeCache {
        private const int InitialCapacity = (1 << 16);

        private Table? m_table;
        private int m_capacity = InitialCapacity;

        // The probes cached.
        public int Count => (Volatile.Read(location: ref m_table)?.Count ?? 0);

        // The bytes the cache holds: its table's arrays and every block an entry names, each block once. Read without
        // the gate, so a batch landing meanwhile can leave it a block off.
        public (long Table, long Blocks, int Capacity) Bytes() {
            if (Volatile.Read(location: ref m_table) is not { } table) { return (0, 0, 0); }

            var capacity = table.Keys.Length;
            var tableBytes = (((long)capacity) * ((((3 * sizeof(int)) + sizeof(int)) + sizeof(int)) + System.Runtime.CompilerServices.Unsafe.SizeOf<ProbeData>()));
            var blocks = new HashSet<object>(comparer: ReferenceEqualityComparer.Instance);
            var blockBytes = 0L;

            for (var slot = 0; (slot < capacity); slot++) {
                if (Volatile.Read(location: ref table.Used[slot]) == 0) { continue; }

                var entry = table.Entries[slot];

                if ((entry.Hits is { } hits) && blocks.Add(item: hits)) { blockBytes += hits.Bytes; }
                if ((entry.Shading is { } shading) && blocks.Add(item: shading)) { blockBytes += shading.Bytes; }
            }

            return (tableBytes, blockBytes, capacity);
        }
        // Forgets every probe; the next write starts a table of the capacity the cache last reached.
        public void Clear() {
            if (m_table is { } table) { m_capacity = table.Keys.Length; }
            Volatile.Write(location: ref m_table, value: null);
        }
        // Forgets the probes outside a box of cells, from low inclusive to high exclusive, once the cache holds more probes
        // than the box has cells; the volume's gate is held. The kept probes are published as a new table, as Grow publishes
        // one, so a reader holding the old table reads a consistent, slightly earlier cache, and a batch block no kept
        // probe names is released with the forgotten entries.
        public void Retain((int X, int Y, int Z) low, (int X, int Y, int Z) high) {
            if (m_table is not { } table) { return; }

            var cells = ((((long)(high.X - low.X)) * (high.Y - low.Y)) * (high.Z - low.Z));

            if (table.Count <= cells) { return; }

            var kept = 0;

            for (var slot = 0; (slot < table.Keys.Length); slot++) {
                if ((table.Used[slot] != 0) && Inside(high: high, key: table.Keys[slot], low: low)) { kept++; }
            }

            var capacity = InitialCapacity;

            while (((kept + 1) * 2) > capacity) { capacity *= 2; }

            var compact = new Table(capacity: capacity);

            for (var slot = 0; (slot < table.Keys.Length); slot++) {
                if ((table.Used[slot] == 0) || !Inside(high: high, key: table.Keys[slot], low: low)) { continue; }

                var free = ~Find(key: table.Keys[slot], table: compact);

                compact.Entries[free] = table.Entries[slot];
                compact.Keys[free] = table.Keys[slot];
                compact.Used[free] = 1;
            }

            compact.Count = kept;
            m_capacity = capacity;
            Volatile.Write(location: ref m_table, value: compact);
        }

        private static bool Inside((int X, int Y, int Z) key, (int X, int Y, int Z) low, (int X, int Y, int Z) high) =>
            ((key.X >= low.X) && (key.X < high.X) && (key.Y >= low.Y) && (key.Y < high.Y) && (key.Z >= low.Z) && (key.Z < high.Z));

        public bool ContainsKey((int X, int Y, int Z) key) => ((Volatile.Read(location: ref m_table) is { } table) && (Find(key: key, table: table) >= 0));
        public bool TryGetValue((int X, int Y, int Z) key, out ProbeData value) {
            var table = Volatile.Read(location: ref m_table);
            var slot = ((table is null) ? -1 : Find(key: key, table: table));

            if ((table is null) || (slot < 0)) {
                value = default;

                return false;
            }

            ref var version = ref table.Versions[slot];

            while (true) {
                var before = Volatile.Read(location: ref version);

                if ((before & 1) != 0) {
                    Thread.SpinWait(iterations: 1);
                    continue;
                }

                value = table.Entries[slot];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(location: ref version) == before) { return true; }
            }
        }

        // Writes a cell's entry; the volume's gate is held.
        public ProbeData this[(int X, int Y, int Z) key] {
            set {
                var table = m_table;

                if (table is null) {
                    table = new Table(capacity: m_capacity);
                    Volatile.Write(location: ref m_table, value: table);
                }

                var slot = Find(key: key, table: table);

                if (slot >= 0) {
                    ref var version = ref table.Versions[slot];

                    _ = Interlocked.Increment(location: ref version);
                    table.Entries[slot] = value;
                    Volatile.Write(location: ref version, value: (version + 1));

                    return;
                }
                if (((table.Count + 1) * 2) > table.Keys.Length) {
                    table = Grow(table: table);
                    slot = Find(key: key, table: table);
                }

                slot = ~slot;
                table.Entries[slot] = value;
                table.Keys[slot] = key;
                Volatile.Write(location: ref table.Used[slot], value: 1);
                table.Count++;
            }
        }

        // A table of twice the capacity holding every entry, published before the write that needed it.
        private Table Grow(Table table) {
            var grown = new Table(capacity: (table.Keys.Length * 2));

            for (var slot = 0; (slot < table.Keys.Length); slot++) {
                if (table.Used[slot] == 0) { continue; }

                var free = ~Find(key: table.Keys[slot], table: grown);

                grown.Entries[free] = table.Entries[slot];
                grown.Keys[free] = table.Keys[slot];
                grown.Used[free] = 1;
            }

            grown.Count = table.Count;
            Volatile.Write(location: ref m_table, value: grown);

            return grown;
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

        private sealed class Table(int capacity) {
            public readonly (int X, int Y, int Z)[] Keys = new (int X, int Y, int Z)[capacity];
            public readonly int[] Used = new int[capacity];
            public readonly int[] Versions = new int[capacity];
            public readonly ProbeData[] Entries = new ProbeData[capacity];

            public int Count;
        }
    }
    // One level of the volume: its probes by cell, its window's first cell, the slots of its window still lacking a
    // measurement of their own cell, the slots composed since its last publication, and the window views read. Its
    // state is the volume's gate's, except the cache, which readers take under its own sequence locks.
    private sealed class Level(int index) {
        public int Index { get; } = index;
        public ProbeCache Cache { get; } = new();
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
    // A tier's volume: its levels (LevelsOf), each level's slots after the finer levels' in the volume, whether hits
    // reflect the bounce the volume holds about them (any bounce the frame asks for), and the reach of its rays (the
    // frame's far distance).
    private readonly record struct Layout(SdfMeshIrradianceLevel[] Levels, bool Bounce, float Reach) {
        public Vector3 Spacing => Levels[0].Spacing;
        public (int X, int Y, int Z) Dims => Levels[0].Dims;

        public Vector3 SpacingOf(int level) => Levels[level].Spacing;
        public (int X, int Y, int Z) DimsOf(int level) => Levels[level].Dims;
        public int CountOf(int level) => Levels[level].Count;
        public int OffsetOf(int level) {
            var offset = 0;

            for (var finer = 0; (finer < level); finer++) { offset += CountOf(level: finer); }
            return offset;
        }

        public int TotalCount => OffsetOf(level: SdfMeshIrradianceProbe.Levels);

        public SdfMeshIrradianceWindow WindowOf(int level, (int X, int Y, int Z) low) =>
            new(Dims: DimsOf(level: level), Low: low, Spacing: SpacingOf(level: level));
        public static Layout Of(SdfIndirectTier tier, int? bounces, float reach) => new(
            Bounce: ((bounces ?? 1) > 0),
            Levels: ((tier == SdfIndirectTier.High) ? HighLevels : MediumLevels),
            Reach: reach
        );
    }
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
        public required long CapturedAt { get; init; }
        public bool Direct { get; init; }
        public bool Emission { get; init; }
        public bool Feedback { get; init; }
        public required SdfIndirectGains Gains { get; init; }
        public required SdfLight[] Lights { get; init; }
        // Each directional light's index in the frame's lights table, beside it in Lights.
        public required int[] LightIndices { get; init; }
        public required SdfMaterial[] Materials { get; init; }
        public required TracedLight[] Points { get; init; }
        public required SdfProgram Program { get; init; }
        public bool Sky { get; init; }
        // The sky's radiance about each hit texel's ray, gain applied (SdfMeshProbeDepth.SkyRadiance) and snapped on the
        // authored grid, zero with the sky source off: what a probe's open ray carries, and the only sky a relight or a
        // meter reads, so it is also the capture's identity (SkyMatches).
        public required Vector3[] SkyTexels { get; init; }
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
        // Captures a frame's lighting; the materials are the previous capture's while the program is the same one.
        // The workspace is the caller's, refilled by every capture; a capture keeps only copies of what it holds.
        public static Lighting Capture(SdfFrame frame, SdfLights captureLights, Lighting? previous, int version, int localVersion, CaptureScratch scratch) {
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
                } else if (light.IsTraced && (light.Weight > 0f) && (light.Param > 0f)) {
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

            return new Lighting {
                Version = version,
                LocalVersion = localVersion,
                CapturedAt = Stopwatch.GetTimestamp(),
                SkyPeak = skyPeak,
                Direct = ((sources & SdfIndirectSources.Direct) != 0),
                Emission = ((sources & SdfIndirectSources.Emission) != 0),
                Feedback = ((sources & SdfIndirectSources.Feedback) != 0),
                Gains = frame.IndirectGains,
                Lights = [.. lights],
                LightIndices = [.. indices],
                Program = frame.Program,
                Materials = (ReferenceEquals(objA: previous?.Program, objB: frame.Program) ? previous!.Materials : [.. frame.Program.Appearance.Materials]),
                Points = [.. points],
                Sky = ((sources & SdfIndirectSources.Sky) != 0),
                SkyTexels = skyTexels,
            };
        }
    }
}
