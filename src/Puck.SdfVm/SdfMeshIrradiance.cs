using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One level's window of the mesh path's irradiance volume (<see cref="SdfMeshIrradiance"/>).</summary>
/// <param name="Spacing">The distance between neighbouring probes, in metres; zero for a level not published.</param>
/// <param name="Dims">The probes along each axis of the window.</param>
/// <param name="Low">The window's first cell: a probe of cell <c>c</c> sits at <c>(c + 0.5) * Spacing</c>.</param>
public readonly record struct SdfMeshIrradianceWindow(float Spacing, (int X, int Y, int Z) Dims, (int X, int Y, int Z) Low) {
    /// <summary>Gets whether the window holds probes views read.</summary>
    public bool Present => ((Spacing > 0f) && (Dims.X > 0) && (Dims.Y > 0) && (Dims.Z > 0));
    /// <summary>Gets the probes the window holds.</summary>
    public int Count => ((Dims.X * Dims.Y) * Dims.Z);
}
/// <summary>The pass-block parameters of the mesh path's irradiance volume (<see cref="SdfMeshIrradiance"/>): the finest
/// level's window, and the coarser levels' about it.</summary>
/// <param name="Enabled">Whether views read the volume; false until a level has measured probes to read.</param>
/// <param name="Spacing">The finest level's distance between neighbouring probes, in metres.</param>
/// <param name="Dims">The probes along each axis of the finest level.</param>
/// <param name="Low">The finest level's first cell: a probe of cell <c>c</c> sits at <c>(c + 0.5) * Spacing</c>.</param>
/// <param name="Middle">The second level's window.</param>
/// <param name="Far">The coarsest level's window.</param>
public readonly record struct SdfMeshIrradianceParameters(bool Enabled, float Spacing, (int X, int Y, int Z) Dims, (int X, int Y, int Z) Low,
    SdfMeshIrradianceWindow Middle = default, SdfMeshIrradianceWindow Far = default) {
    /// <summary>Returns a level's window, finest first.</summary>
    /// <param name="level">The level, from zero to <see cref="SdfMeshIrradianceProbe.Levels"/> less one.</param>
    /// <returns>The window; a default window for a level out of range.</returns>
    public SdfMeshIrradianceWindow Window(int level) => level switch {
        0 => new SdfMeshIrradianceWindow(Dims: Dims, Low: Low, Spacing: Spacing),
        1 => Middle,
        2 => Far,
        _ => default,
    };
}
/// <summary>
/// The baked-mesh path's indirect lighting: a volume of irradiance probes about the first view's camera, traced on the
/// CPU against the frame's static meshes (<see cref="SdfMeshIrradianceScene"/>) and read by the views pass, which performs
/// no field query and traces nothing.
/// <para>A probe sits at the centre of a world cell, one <see cref="SdfMeshIrradianceParameters.Spacing"/> wide. It stores
/// an ambient cube: for each of the six axis directions the share of cosine-weighted sky its rays reach (the views pass
/// multiplies it by the live sky irradiance, so the sky stays live) and the cosine-weighted mean radiance the static
/// geometry sends it (the directional lights and the static point and spot lights under traced shadows, emission, and,
/// from the second sweep, the light the geometry reflects from the probes around each hit, its sky included). It also
/// stores which lights of the table its centre sees, one bit a static point or spot light (<see cref="SdfLight.IsTraced"/>),
/// which the views pass interpolates into each such light's visibility at a surface, so a fixture lights nothing
/// through the wall or slab between them, and an octahedral map of first-hit surface support. The receiver's direction
/// and distance sample that map through <see cref="SdfMeshProbeDepth.Visibility"/>. A probe whose rays mostly meet
/// back faces lies inside geometry and is stored
/// as invalid. Each level's window is toroidal: a cell's probe lives in the slot its coordinates name modulo the window's
/// size, keyed by its cell, so the views pass rejects a slot holding another cell's probe, and a move of the camera
/// rewrites only the slots of cells it brings in.</para>
/// <para>The volume has <see cref="SdfMeshIrradianceProbe.Levels"/> nested levels about the camera, each window a
/// coarser lattice over a wider reach (<see cref="LevelSpacing"/>), traced alike against the same scene. A receiver
/// reads the finest level whose window holds it, the coarsest past every window; where none of that level's probes
/// measured it, the same probes answer past their measured reach, blocked only by the surfaces they measured, so no
/// surface the camera sees reads the absence of a measurement as darkness.</para>
/// <para>Probes are traced on the thread pool, nearest the camera first and every probe's first sweep before any second,
/// and cached by cell, so a static world traces each cell once per lighting: a camera returning to a place reads its
/// cached probes at once. A change of the static draws rebuilds the scene from the one it replaces, building only the
/// geometry new to it, and retraces every probe; a change of the directional lights, the static point and spot lights,
/// the gains, the sources, the bounces or what the program's materials reflect and emit retraces them over the same
/// scene. Until a probe lands, the old one keeps drawing. The dynamic draws (moving bodies) receive the volume's light
/// and do not cast into it; an anchored light, which moves, is neither traced nor occluded.</para>
/// </summary>
public sealed class SdfMeshIrradiance : IDisposable {
    private bool m_disposed;
    private bool m_trimListening;

    private void TrimRetained() {
        lock (m_gate) {
            if (m_disposed || (m_scene is null)) { return; }
            var previous = m_scene;

            m_scene = previous.WithoutRetained();
            previous.Dispose();
        }
    }

    /// <summary>Stops new work, joins scene and trace borrowers, then releases the last scene.</summary>
    public void Dispose() {
        Task[] jobs;

        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            if (m_trimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested -= TrimRetained; m_trimListening = false; }
            m_owed = m_refillOwed = false;
            jobs = new[] { m_build, m_refill, m_worker }.OfType<Task>().ToArray();
        }
        try { Task.WhenAll(jobs).GetAwaiter().GetResult(); } finally { lock (m_gate) { m_scene?.Dispose(); m_scene = null; m_materialsScene = null; m_diagnosticDraws = []; } }
    }

    /// <summary>The most probes a volume holds, the High tier's: its finest window and the coarser levels'.</summary>
    public const int MaxProbes = (((48 * 16) * 48) + ((SdfMeshIrradianceProbe.Levels - 1) * ((32 * 12) * 32)));

    /// <summary>The probe spacing, in metres, of each level past the finest, whatever the tier: a window of 32 cells
    /// reaches 256 metres at the second level and 1,024 at the third.</summary>
    public static ReadOnlySpan<float> LevelSpacing => [8f, 32f];

    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-gi";

    /// <summary>Counts each trace scene built for a change of the static draws.</summary>
    public static readonly WorkKind Scenes = new(name: "sdf.mesh-gi.scenes", unit: "scenes", workClass: WorkClass.Pacing);
    /// <summary>Counts each geometry a scene build built: one no earlier scene held.</summary>
    public static readonly WorkKind GeometriesBuilt = new(name: "sdf.mesh-gi.geometries-built", unit: "geometries", workClass: WorkClass.Pacing);
    /// <summary>Counts each geometry a scene build took from the scene it replaced.</summary>
    public static readonly WorkKind GeometriesReused = new(name: "sdf.mesh-gi.geometries-reused", unit: "geometries", workClass: WorkClass.Pacing);
    /// <summary>Counts the triangles of the geometries scene builds built.</summary>
    public static readonly WorkKind TrianglesBuilt = new(name: "sdf.mesh-gi.triangles-built", unit: "triangles", workClass: WorkClass.Pacing);
    /// <summary>Counts each probe sweep traced.</summary>
    public static readonly WorkKind Probes = new(name: "sdf.mesh-gi.probes", unit: "sweeps", workClass: WorkClass.Pacing);
    /// <summary>Counts camera radiance traces, including rejected samples inside opaque geometry.</summary>
    public static readonly WorkKind CameraTraces = new(name: "sdf.mesh-gi.camera-traces", unit: "samples", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [Scenes, GeometriesBuilt, GeometriesReused, TrianglesBuilt, Probes, CameraTraces];

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// volume exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Gets the volume's counts: its scene builds, the geometry they built and reused, and its probe sweeps.</summary>
    public WorkCounterSet Counters { get; } = new(kinds: DeclaredKinds, name: SourceName);

    private const int HighRays = 128;
    private const int MediumRays = 96;

    private static readonly Vector3[] PlacementDirections = BuildDirections(count: 32);
    private static readonly Vector3[] MediumDirections = BuildDirections(count: MediumRays);
    private static readonly Vector3[] HighDirections = BuildDirections(count: HighRays);

    private static Vector3[] BuildDirections(int count) {
        var directions = new Vector3[count];

        for (var ray = 0; (ray < count); ray++) { directions[ray] = Fibonacci(count: count, index: ray); }
        return directions;
    }

    private static readonly (int X, int Y, int Z) HighDims = (48, 16, 48);
    private static readonly (int X, int Y, int Z) MediumDims = (32, 12, 32);

    // A probe whose rays meet back faces more than this share of the time lies inside geometry.
    private const float InsideShare = 0.25f;
    // The probes one batch of the trace takes before it looks again at the camera and the lighting.
    private const int BatchProbes = 384;

    // The trace's workers: a lockstep frame waits for its window traced to the end, so it traces on all but two of the
    // machine's processors; a trace behind rendered frames takes a quarter of them, so the frame thread keeps a core of
    // its own and the memory bandwidth it packs and uploads with.
    private static readonly int DrainWorkers = Math.Max(val1: 1, val2: (Environment.ProcessorCount - 2));
    private static readonly int BackgroundWorkers = Math.Max(val1: 1, val2: (Environment.ProcessorCount / 4));

    // A shadow ray toward a point light ends this far before it, in metres, so a fixture sunk a little into its housing
    // is not blocked by it.
    private const float LightBias = 0.02f;

    /// <summary>The weight every axis of the meter's reading takes besides its facing (<see cref="TryMeter"/>): the
    /// surround a center-weighted meter still reads.</summary>
    public const float MeterSurround = 0.05f;

    // The meter's sky visibility over the mean open share of a probe's six axes: on open ground the up axis sees the
    // whole sky, the four sideways axes half of it and the down axis none, a mean of one half.
    private const float OpenGroundShare = 2f;

    // The least wall time between two scene builds while the bake still lands opaque static meshes, so the trace
    // measures what has landed without restarting on every arrival. Presentation only: the scene a frame lights by is
    // never simulation state.
    private static readonly TimeSpan PendingSceneInterval = TimeSpan.FromSeconds(value: 4d);
    private readonly Lock m_gate = new();
    private readonly Level[] m_levels = CreateLevels();

    private static Level[] CreateLevels() {
        var levels = new Level[SdfMeshIrradianceProbe.Levels];

        for (var index = 0; (index < levels.Length); index++) { levels[index] = new Level(index: index); }
        return levels;
    }

    /// <summary>Describes the probe caches the volume keeps across window moves, over every level: their probes, their
    /// tables' capacity and bytes, and the bytes of the probe blocks their entries hold. Safe to read from any thread; a
    /// trace landing meanwhile can leave it a block off.</summary>
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

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"probe-cache-probes={count} probe-cache-capacity={capacity} probe-cache-table={table}B probe-cache-blocks={blocks}B probe-cache-bytes={(table + blocks)}B");
    }

    // The trace's candidates and a batch's results, kept across batches and traces: one trace runs at a time.
    private readonly List<(int Level, (int X, int Y, int Z) Cell, int Sweeps, float Distance)> m_candidates = [];
    private ProbeData[] m_results = [];
    // The trace's store of a batch's landed probes (StoreLanded): the cells that landed, the window cells they restore,
    // and the slot words composed for those.
    private readonly List<(int Level, (int X, int Y, int Z) Cell)> m_landed = [];
    private readonly List<(int Level, (int X, int Y, int Z) Cell)> m_restoreCells = [];
    private uint[] m_restoreWords = [];
    private uint[] m_volume = [];
    private readonly bool[] m_dirtySlots = new bool[MaxProbes];
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
    private Task? m_build;
    private Lighting? m_lighting;
    private int m_lightingKey;
    private SdfProgram? m_materialsProgram;
    private SdfMeshIrradianceScene? m_materialsScene;
    private int m_materialsKey;
    private int m_generation;
    private Layout m_layout;
    private SdfMeshIrradianceParameters m_published;
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
    // Whether the windows may hold probes the trace has not yet traced: set when the scene, the lighting or a window
    // changes, cleared when a trace finds every probe of every window traced.
    private bool m_owed;

    // Whether a level holds slots composed since its last publication.
    private bool Dirty {
        get {
            foreach (var level in m_levels) {
                if (level.Changed.Count > 0) { return true; }
            }
            return false;
        }
    }

    // Whether the latest Stage wrote the volume table: the frame it prepared must render even once the trace is done.
    private bool m_wrote;
    private bool m_reported;
    private long m_traced;
    private long m_rays;
    private long m_depthRays;

    /// <summary>Gets the brightest luminance, in nits, of the sky the probes' lighting sees over its environment map;
    /// zero while no lighting is captured or the sky is no source of it.</summary>
    public float SkyPeakLuminance {
        get {
            lock (m_gate) {
                return (m_lighting?.SkyPeak ?? 0f);
            }
        }
    }
    /// <summary>Gets whether probes are being traced or wait to reach the volume table.</summary>
    public bool Settling {
        get {
            lock (m_gate) {
                // A faulted trace settles nothing: the next Stage reports it.
                return ((m_worker is { IsCompleted: false } or { IsFaulted: true }) || (m_build is { IsCompleted: false }) || ((m_scene is not null) && m_owed) || m_refillOwed || Dirty || m_wrote);
            }
        }
    }

    /// <summary>Advances the volume for a frame of the mesh path before the frame's upload: rebuilds the scene when the
    /// static draws changed (while the bake still lands opaque static meshes, from what has landed, once the previous
    /// scene's trace has finished), restarts the trace on a lighting change, moves each level's window with the views'
    /// cameras, starts the trace of the probes the windows lack, and writes the probes that landed into the tables'
    /// volume table.</summary>
    /// <param name="frame">The frame, its baked draws dressed in.</param>
    /// <param name="tables">The residency's tables.</param>
    /// <param name="tier">The indirect tier the views asked for; Off reads no volume.</param>
    /// <param name="bakePending">Whether the mesh bake still owes opaque static meshes: a lockstep frame then builds no
    /// scene, and any other frame builds one from the meshes landed so far at most every few seconds.</param>
    /// <returns>The parameters the views pass reads.</returns>
    public SdfMeshIrradianceParameters Stage(SdfFrame frame, SdfWorldTables tables, SdfIndirectTier tier, bool bakePending) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        // The writer is one delegate to a method of this instance, so a steady frame allocates nothing here.
        m_writeTables = tables;
        m_write ??= WriteToTables;

        return Stage(bakePending: bakePending, frame: frame, tier: tier, write: m_write);
    }

    private void WriteToTables(ReadOnlyMemory<uint> words) {
        m_writeTables!.FitMeshIrradiance(wordCount: words.Length);
        m_changedSlots.Sort();
        for (var index = 0; (index < m_changedSlots.Count);) {
            var first = m_changedSlots[index++];
            var end = (first + 1);

            while ((index < m_changedSlots.Count) && (m_changedSlots[index] == end)) { index++; end++; }
            var offset = (first * SdfMeshIrradianceProbe.ProbeWords);

            m_writeTables!.WriteMeshIrradiance(words.Span.Slice(length: ((end - first) * SdfMeshIrradianceProbe.ProbeWords), start: offset), offset);
        }
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

        // A lockstep frame must trace its own scene, not whichever asynchronous build happened to land first.
        // Wait outside the gate: the builder takes it to publish. A build already in flight may belong to an older
        // frame, so resolve again after it lands until this frame's static draws are the installed scene.
        if (frame.TickLocked && !bakePending) {
            while (true) {
                Task? build;

                lock (m_gate) {
                    build = RefreshScene(frame: frame);
                }
                if (build is null) { break; }
                build.GetAwaiter().GetResult();
            }
        }

        var layout = Layout.Of(tier: tier, bounces: frame.IndirectBounces);
        bool drain;

        lock (m_gate) {
            m_wrote = false;
            if (!bakePending || ScenePendingDue(frame: frame)) { _ = RefreshScene(bakePending: bakePending, frame: frame); }
            RefreshLighting(frame: frame, layout: layout);
            m_camera = WindowCamera(frame: frame);
            if (m_cameraSamples.Length != frame.Views.Count) {
                m_cameraSamples = new CameraSample?[frame.Views.Count];
                m_nextCameraSample = 0;
            }

            var resizing = (!m_windowValid || (layout.Dims != m_layout.Dims) || (layout.Spacing != m_layout.Spacing));

            if (resizing) {
                m_volume = new uint[(layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords)];
                Array.Clear(array: m_dirtySlots);
                m_changedSlots.Clear();
                foreach (var level in m_levels) {
                    level.Cache.Clear();
                    level.Changed.Clear();
                    level.Published = default;
                }
                m_published = default;
                m_generation++;
                m_reported = false;
            }
            m_layout = layout;

            var moved = resizing;

            foreach (var level in m_levels) {
                var low = WindowLow(current: level.Low, frame: frame, layout: layout, level: level.Index, resizing: resizing);

                if (resizing || (low != level.Low)) {
                    level.Low = low;
                    moved = true;
                }
            }
            if (moved) {
                m_windowValid = true;
                m_owed = true;
                OweRefill(pooled: !frame.TickLocked);
            }
            drain = (frame.TickLocked && m_owed && (m_scene is not null) && (m_lighting is not null));
        }

        // A lockstep frame refills its window before it renders, waiting for a refill in flight, which takes the gate.
        if (frame.TickLocked) {
            m_refill?.Wait();
            Refill();
        }

        // A lockstep frame renders the window traced to the end, so its indirect light and its views' exposure meters
        // never depend on how far an asynchronous trace had got by the time the frame was asked for. The trace takes the
        // gate itself, so it runs outside it, after the worker in flight, which may hold it, has finished.
        if (drain) {
            try {
                m_worker?.Wait();
            } catch (AggregateException) {
                // A faulted worker is reported below, as it is for any frame.
            }
            if (m_worker is not { IsFaulted: true }) {
                Trace(workers: DrainWorkers);
            }
        }

        lock (m_gate) {
            if (m_worker is { IsFaulted: true } faulted) {
                m_worker = null;
                throw new InvalidOperationException(message: "The irradiance volume's trace failed.", innerException: faulted.Exception);
            }
            if (m_owed && (m_scene is not null) && (m_lighting is not null) && (m_worker is null or { IsCompleted: true })) {
                m_worker = Task.Run(action: () => Trace(workers: BackgroundWorkers));
            }
            Publish(layout: layout, write: write);
            return m_published;
        }
    }

    // Publishes each level whose window has every slot measured: the slots composed since its last publication reach the
    // table in one write with every other ready level's, and its window's coordinates reach the parameters. A level with
    // a slot still unmeasured keeps its previous window and table words, since publishing an empty slot would invent
    // darkness and filling it with sky would invent a leak. The volume is disabled until a level first publishes, and
    // views then light as without it.
    private void Publish(Layout layout, Action<ReadOnlyMemory<uint>> write) {
        foreach (var level in m_levels) {
            var window = layout.WindowOf(level: level.Index, low: level.Low);

            if ((level.Unwritten > 0) || ((level.Changed.Count == 0) && (level.Published == window))) { continue; }
            m_changedSlots.AddRange(collection: level.Changed);
            level.Changed.Clear();
            level.Published = window;
            m_wrote = true;
        }
        if (m_changedSlots.Count > 0) {
            write(obj: m_volume.AsMemory(length: (layout.TotalCount * SdfMeshIrradianceProbe.ProbeWords), start: 0));
            foreach (var slot in m_changedSlots) { m_dirtySlots[slot] = false; }
            m_changedSlots.Clear();
        }

        var enabled = false;

        foreach (var level in m_levels) { enabled |= level.Published.Present; }

        var finest = PublishedOf(layout: layout, level: 0);

        m_published = new SdfMeshIrradianceParameters(Dims: finest.Dims, Enabled: enabled, Far: PublishedOf(layout: layout, level: 2), Low: finest.Low,
            Middle: PublishedOf(layout: layout, level: 1), Spacing: finest.Spacing);
    }
    // A level's published window; one not yet published carries its probe counts at zero spacing, so the views pass skips
    // it yet places the coarser levels' slots after its own.
    private SdfMeshIrradianceWindow PublishedOf(Layout layout, int level) => (m_levels[level].Published.Present
        ? m_levels[level].Published
        : new SdfMeshIrradianceWindow(Dims: layout.DimsOf(level: level), Low: default, Spacing: 0f));
    // The first cell of a level's window. It centres on the box of the cells of every view's camera this residency
    // renders when that box fits in the window's middle, and on the first view's camera alone when it does not, so each
    // level follows every view as far as its reach allows and a coarser level covers views a finer one cannot. It
    // recentres only once a camera it centres on leaves its middle, an eighth of the window short of each face, so a
    // camera that moves back and forth across a cell (a body falling through a floor painting and out of the ceiling's)
    // keeps one window rather than moving it, refilling it and tracing its new probes every frame.
    private (int X, int Y, int Z) WindowLow(SdfFrame frame, int level, Layout layout, (int X, int Y, int Z) current, bool resizing) {
        var spacing = layout.SpacingOf(level: level);
        var dims = layout.DimsOf(level: level);
        var first = CellOf(position: m_camera, spacing: spacing);
        var low = first;
        var high = first;

        for (var index = 1; (index < frame.Views.Count); index++) {
            var view = frame.Views[index];
            var position = view.Camera.Position;

            if (view.Elsewhere || !float.IsFinite(f: position.X) || !float.IsFinite(f: position.Y) || !float.IsFinite(f: position.Z)) { continue; }

            var cell = CellOf(position: position, spacing: spacing);

            low = (Math.Min(val1: low.X, val2: cell.X), Math.Min(val1: low.Y, val2: cell.Y), Math.Min(val1: low.Z, val2: cell.Z));
            high = (Math.Max(val1: high.X, val2: cell.X), Math.Max(val1: high.Y, val2: cell.Y), Math.Max(val1: high.Z, val2: cell.Z));
        }
        if (!Fits(dims: dims.X, high: high.X, low: low.X) || !Fits(dims: dims.Y, high: high.Y, low: low.Y) || !Fits(dims: dims.Z, high: high.Z, low: low.Z)) {
            low = first;
            high = first;
        }
        if (!resizing && InMiddle(cell: low, dims: dims, low: current) && InMiddle(cell: high, dims: dims, low: current)) {
            return current;
        }

        return ((((low.X + high.X) >> 1) - (dims.X / 2)), (((low.Y + high.Y) >> 1) - (dims.Y / 2)), (((low.Z + high.Z) >> 1) - (dims.Z / 2)));
    }
    private static (int X, int Y, int Z) CellOf(Vector3 position, float spacing) =>
        (((int)MathF.Floor(x: (position.X / spacing))), ((int)MathF.Floor(x: (position.Y / spacing))), ((int)MathF.Floor(x: (position.Z / spacing))));
    // Whether cells from low to high fit in a window's middle along one axis.
    private static bool Fits(int low, int high, int dims) => ((high - low) < (dims - (2 * Math.Max(val1: 1, val2: (dims / 8)))));

    /// <summary>Reads the volume as a center-weighted reflected-light meter at a camera: the luminance the traced probes
    /// about the camera see along each of their six axes, the light the surfaces about it reflect and emit under every
    /// traced light and the sky through the openings it sees, averaged with each axis weighted by the squared cosine of
    /// its angle to where the camera looks plus <see cref="MeterSurround"/>, so the axis ahead counts about four fifths of
    /// the reading. When no visible lattice probe answers, reads a camera radiance sample cached by cell and generation,
    /// refreshed when the bounce trace finishes. Reuse within a cell requires visibility from the sample's origin.
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
    /// <returns><see langword="false"/> while no scene is available or the camera lies inside opaque geometry.</returns>
    public bool TryMeter(Vector3 position, Vector3 forward, out float luminance, out float skyVisibility, out SdfExposureMeterSource source, bool fresh = false) {
        luminance = 0f;
        skyVisibility = 1f;
        source = SdfExposureMeterSource.None;

        lock (m_gate) {
            if ((m_lighting is not { } lighting) || !m_windowValid) {
                return false;
            }

            var spacing = m_layout.Spacing;
            var cache = m_levels[0].Cache;
            var sample = ((position / spacing) - new Vector3(value: 0.5f));
            var floor = new Vector3(x: MathF.Floor(x: sample.X), y: MathF.Floor(x: sample.Y), z: MathF.Floor(x: sample.Z));
            var fraction = (sample - floor);
            Span<float> axes = stackalloc float[6];
            var open = 0f;
            var total = 0f;

            for (var corner = 0; (corner < 8); corner++) {
                var offset = new Vector3(x: corner & 1, y: (corner >> 1) & 1, z: (corner >> 2) & 1);
                var cell = (((int)(floor.X + offset.X)), ((int)(floor.Y + offset.Y)), ((int)(floor.Z + offset.Z)));

                if (!cache.TryGetValue(key: cell, value: out var cached)) {
                    continue;
                }
                var probe = Readable(probe: cached);

                if (probe.Invalid) { continue; }

                var probePoint = ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * spacing) + probe.Offset);
                var toward = (probePoint - position);
                var distance = toward.Length();

                if ((distance > 1.0e-3f) && (m_scene is { } scene) && !scene.Unblocked(direction: (toward / distance), maximum: distance, origin: position)) { continue; }

                var weight = ((((offset.X > 0f) ? fraction.X : (1f - fraction.X)) * ((offset.Y > 0f) ? fraction.Y : (1f - fraction.Y))) * ((offset.Z > 0f) ? fraction.Z : (1f - fraction.Z)));

                if (weight <= 0f) {
                    continue;
                }

                for (var axis = 0; (axis < 6); axis++) {
                    var sky = (lighting.Sky ? (lighting.SkyIrradiance(normal: Axes[axis]) * (probe.Sky[axis] * lighting.Gains.Sky)) : Vector3.Zero);

                    axes[axis] += (SdfPhotometry.LuminanceOf(color: (probe.Radiance[axis] + sky)) * weight);
                    open += ((probe.Sky[axis] * weight) / 6f);
                }
                total += weight;
            }

            if (total <= 0f) {
                if (m_scene is not { } scene) { return false; }

                var sampleAtCamera = CameraRadiance(fresh: fresh, lighting: lighting, position: position, scene: scene);

                if (sampleAtCamera.Invalid) { return false; }
                for (var axis = 0; (axis < 6); axis++) {
                    var skyLight = (lighting.Sky ? (lighting.SkyIrradiance(normal: Axes[axis]) * (sampleAtCamera.Sky[axis] * lighting.Gains.Sky)) : Vector3.Zero);

                    axes[axis] = SdfPhotometry.LuminanceOf(color: (sampleAtCamera.Radiance[axis] + skyLight));
                    open += (sampleAtCamera.Sky[axis] / 6f);
                }
                total = 1f;
                source = SdfExposureMeterSource.CameraTrace;
            } else {
                source = SdfExposureMeterSource.Probes;
            }

            var sum = 0f;
            var weights = 0f;

            for (var axis = 0; (axis < 6); axis++) {
                var ahead = MathF.Max(x: 0f, y: Vector3.Dot(vector1: Axes[axis], vector2: forward));
                var facing = (MeterSurround + (ahead * ahead));

                sum += ((axes[axis] / total) * facing);
                weights += facing;
            }

            luminance = (sum / weights);
            skyVisibility = Math.Clamp(max: 1f, min: 0f, value: ((OpenGroundShare * open) / total));

            return true;
        }
    }

    private CameraSample CameraRadiance(Vector3 position, SdfMeshIrradianceScene scene, Lighting lighting, bool fresh) {
        var cell = CellOf(position: position, spacing: m_layout.Spacing);

        foreach (var sample in m_cameraSamples) {
            if ((sample is null) || (sample.Generation != m_generation) || (sample.Cell != cell) || (sample.Settled != !m_owed)) { continue; }
            if (sample.Position == position) { return sample; }
            if (fresh || sample.Invalid) { continue; }

            var toward = (position - sample.Position);
            var distance = toward.Length();

            if (scene.Unblocked(direction: (toward / distance), maximum: distance, origin: sample.Position)) { return sample; }
        }

        // A cell can straddle a partition. A sample belongs to its actual origin, never an assumed open cell centre.
        var sky = new float[6];
        var radiance = new Vector3[6];
        var valid = TraceRadiance(origin: position, layout: m_layout, lighting: lighting,
            lows: Lows(), scene: scene, sweep: (m_layout.Sweeps - 1), sky: sky, radiance: radiance, inside: out _);
        var traced = new CameraSample(Cell: cell, Generation: m_generation, Invalid: !valid, Position: position,
            Radiance: radiance, Settled: !m_owed, Sky: sky);

        Counters.Count(kind: CameraTraces);
        m_cameraSamples[m_nextCameraSample] = traced;
        m_nextCameraSample = ((m_nextCameraSample + 1) % m_cameraSamples.Length);
        return traced;
    }

    /// <summary>Describes the volume: its tier's layout, the static scene, the lights it traces, the probes cached and
    /// the rays traced.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        lock (m_gate) {
            var buffers = (m_writeTables?.MeshIrradianceBufferBytes ?? default);

            var cached = 0;

            foreach (var level in m_levels) { cached += level.Cache.Count; }

            return $"[sdf.mesh-gi] spacing={m_layout.Spacing:0.##} dims={m_layout.Dims.X}x{m_layout.Dims.Y}x{m_layout.Dims.Z} levels={string.Join(separator: ',', values: LevelSpacing.ToArray().Prepend(element: m_layout.Spacing).Select(selector: static spacing => spacing.ToString(format: "0.##", provider: System.Globalization.CultureInfo.InvariantCulture)))} rays={m_layout.Rays} sweeps={m_layout.Sweeps} triangles={(m_scene?.TriangleCount ?? 0)} lights={(m_lighting?.Points.Length ?? 0)} cached={cached} traced={m_traced} traced-rays={m_rays} depth-rays={m_depthRays} record-bytes={(SdfMeshIrradianceProbe.ProbeWords * sizeof(uint))} volume-bytes={(m_volume.LongLength * sizeof(uint))} table-device-bytes={buffers.DeviceLocal} table-upload-bytes={buffers.HostVisible} table-cpu-bytes={(m_writeTables?.MeshIrradianceCpuBytes ?? 0)}";
        }
    }

    // Whether a frame whose bake still lands opaque static meshes builds a scene from what has landed: never a lockstep
    // frame, which renders the whole bake; otherwise once no build runs, the installed scene's trace has finished (or no
    // scene is installed), and PendingSceneInterval has passed since the last build began. The finished trace paces the
    // rebuilds, so the probes always finish measuring one landed set before the next replaces it.
    private bool ScenePendingDue(SdfFrame frame) =>
        (!frame.TickLocked && (m_build is null or { IsCompleted: true }) && ((m_scene is null) || !m_owed) &&
        (!m_sceneClock.IsRunning || (m_sceneClock.Elapsed >= PendingSceneInterval)));
    // Rebuilds the scene on the thread pool when the static draws changed, from the scene it replaces so only geometry new
    // to it builds; the old scene traces until the new one lands. While the bake still owes opaque static meshes, a frame
    // with none landed builds nothing: an empty scene would measure open sky where the walls have yet to land.
    // Returns the build to wait for before this frame can use its scene, or null when it is already installed.
    private Task? RefreshScene(SdfFrame frame, bool bakePending = false) {
        var key = 17;
        var draws = 0;

        var meshDraws = frame.MeshDraws;

        for (var index = 0; (index < meshDraws.Count); index++) {
            var draw = meshDraws[index];

            if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
            key = HashCode.Combine(value1: key, value2: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: draw.Mesh), value3: draw.ObjectToWorld, value4: draw.Material);
            draws++;
        }
        // Even a frame matching the installed scene waits for an older build: that build could otherwise replace
        // the scene while this frame traces it. The lockstep caller resolves this frame again after it lands.
        if (m_build is { IsCompleted: false }) { return m_build; }
        if (key == m_sceneKey) { return null; }
        if (key == m_buildingKey) { return m_build; }
        if (bakePending && (draws == 0)) { return null; }

        return StartSceneBuild(draws: draws, frame: frame, key: key);
    }
    // Apart from RefreshScene, since the build's closure is made on entry to the method holding it, and nearly every frame
    // matches the installed scene.
    private Task StartSceneBuild(SdfFrame frame, int key, int draws) {
        if (!m_trimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested += TrimRetained; m_trimListening = true; }
        var snapshot = frame.MeshDraws.ToArray();
        var previous = m_scene?.Acquire();
        var snapshotClaims = new SdfMeshClaims(meshes: snapshot.Select(selector: static draw => draw.Mesh));

        m_buildingKey = key;
        m_sceneClock.Restart();
        m_build = Task.Run(action: () => {
            using var snapshotBorrow = snapshotClaims;
            using var previousBorrow = previous;
            var clock = Stopwatch.StartNew();
            var scene = SdfMeshIrradianceScene.Build(draws: snapshot, previous: previous);

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
                m_generation++;
                m_owed = true;
                m_reported = false;
                m_clock.Restart();
                m_traced = 0;
                m_rays = 0;
                m_depthRays = 0;
            }
            Console.Error.WriteLine(value: $"[sdf.mesh-gi] scene draws={draws} triangles={(scene?.TriangleCount ?? 0)} geometries={(scene?.GeometryCount ?? 0)} built={(scene?.GeometriesBuilt ?? 0)} built-triangles={(scene?.TrianglesBuilt ?? 0)} reused={(scene?.GeometriesReused ?? 0)} retained-triangles={(scene?.TrianglesRetained ?? 0)} seconds={clock.Elapsed.TotalSeconds:0.000}");
        });
        return m_build;
    }
    // Captures the lighting the probes trace under, and restarts the trace when it changes: the directional lights and
    // the static point and spot lights, whole, so a fixture switched off retraces the bounce it cast.
    private void RefreshLighting(SdfFrame frame, Layout layout) {
        var key = HashCode.Combine(value1: frame.IndirectGains, value2: frame.IndirectSources, value3: layout.Sweeps, value4: layout.Rays, value5: MaterialsKey(program: frame.Program));
        var records = frame.Lights.Records;

        for (var index = 0; (index < frame.Lights.Count); index++) {
            var light = records[index];

            if ((light.Kind == SdfLightKind.Directional) || light.IsTraced) { key = HashCode.Combine(value1: key, value2: index, value3: light); }
        }
        if ((key == m_lightingKey) && (m_lighting is not null)) { return; }

        m_lightingKey = key;
        m_lighting = Lighting.Capture(frame: frame);
        m_generation++;
        m_owed = true;
        m_reported = false;
        m_clock.Restart();
        m_traced = 0;
        m_rays = 0;
        m_depthRays = 0;
    }
    // The part of a program's materials the trace reads (Shade: what each reflects and emits), hashed once per program and
    // scene: the materials the static scene's triangles carry (SdfMeshIrradianceScene.Materials), by id. A program rebuilt
    // with one of them changed in place (a bound color's move, a placement showing another creation over the same meshes)
    // leaves the static draws as they were, so only this key retraces it; a body's palette coming or going, which no
    // probe ray meets, leaves it alone.
    private int MaterialsKey(SdfProgram program) {
        if (ReferenceEquals(objA: program, objB: m_materialsProgram) && ReferenceEquals(objA: m_scene, objB: m_materialsScene)) { return m_materialsKey; }

        var key = 17;
        var materials = program.Materials;

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
        var margin = Math.Max(val1: 1, val2: (dims / 8));

        return ((cell >= (low + margin)) && (cell < ((low + dims) - margin)));
    }
    private bool InWindow(int level, (int X, int Y, int Z) cell) {
        var low = m_levels[level].Low;
        var dims = m_layout.DimsOf(level: level);

        return ((cell.X >= low.X) && (cell.X < (low.X + dims.X)) && (cell.Y >= low.Y) && (cell.Y < (low.Y + dims.Y)) &&
            (cell.Z >= low.Z) && (cell.Z < (low.Z + dims.Z)));
    }
    // Each level's window's first cell, as the trace reads them outside the gate.
    private (int X, int Y, int Z)[] Lows() {
        var lows = new (int X, int Y, int Z)[m_levels.Length];

        for (var level = 0; (level < m_levels.Length); level++) { lows[level] = m_levels[level].Low; }
        return lows;
    }
    private bool SameLows(ReadOnlySpan<(int X, int Y, int Z)> lows) {
        for (var level = 0; (level < m_levels.Length); level++) {
            if (lows[level] != m_levels[level].Low) { return false; }
        }
        return true;
    }
    // Owes the refill of the windows the volume just moved: no level publishes until it lands, and it runs on the thread
    // pool (Refill), or in a lockstep frame on the frame's thread, since a window that jumps far re-stores every slot.
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
                                if (level.Cache.ContainsKey(key: (x, y, z))) { m_refillCells.Add(item: (level.Index, (x, y, z))); }
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
    private Span<uint> SlotWords(int level, (int X, int Y, int Z) cell) {
        var slot = VolumeSlot(cell: cell, level: level);

        if (!m_dirtySlots[slot]) {
            m_dirtySlots[slot] = true;
            m_levels[level].Changed.Add(item: slot);
        }
        return m_volume.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (slot * SdfMeshIrradianceProbe.ProbeWords));
    }
    // Composes a level's cached cell's slot words from its own measured cubes, depth and light visibility;
    // false, writing nothing, when the cache holds no probe of it. It reads the cache alone, so it runs outside the gate.
    private bool Compose(int level, (int X, int Y, int Z) cell, Span<uint> words) {
        if (!m_levels[level].Cache.TryGetValue(key: cell, value: out var cached)) { return false; }

        var probe = Readable(probe: cached);
        var sky = probe.Sky;
        var radiance = probe.Radiance;

        words[0] = (KeyOf(x: cell.X, y: cell.Y, z: cell.Z) | SdfMeshIrradianceProbe.WrittenFlag) | (probe.Invalid ? SdfMeshIrradianceProbe.InvalidFlag : 0u);
        for (var pair = 0; (pair < 3); pair++) {
            words[(1 + pair)] = Pack(low: sky[(2 * pair)], high: sky[((2 * pair) + 1)]);
        }
        for (var pair = 0; (pair < 9); pair++) {
            words[(4 + pair)] = Pack(low: Channel(cube: radiance, index: (2 * pair)), high: Channel(cube: radiance, index: ((2 * pair) + 1)));
        }
        for (var word = 0; (word < SdfMeshIrradianceProbe.LightWords); word++) {
            words[(SdfMeshIrradianceProbe.LightsWord + word)] = probe.Lights[word];
        }
        // KEEP IN SYNC with sdfMeshProbeOffset.
        words[SdfMeshIrradianceProbe.OffsetWord] = Pack(high: probe.Offset.Y, low: probe.Offset.X);
        words[(SdfMeshIrradianceProbe.OffsetWord + 1)] = Pack(high: probe.Inside, low: probe.Offset.Z);
        probe.Depth.CopyTo(destination: words[SdfMeshIrradianceProbe.DepthWord..]);

        return true;
    }
    private static float Channel(ReadOnlySpan<Vector3> cube, int index) {
        var value = cube[(index / 3)];

        return ((index % 3) switch { 0 => value.X, 1 => value.Y, _ => value.Z });
    }
    private static uint Pack(float low, float high) =>
        ((uint)BitConverter.HalfToUInt16Bits(value: ((Half)low))) | (((uint)BitConverter.HalfToUInt16Bits(value: ((Half)high))) << 16);

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

    // The trace loop: batches of the windows' probes that lack a sweep, fewest sweeps first, then finest level first and
    // nearest the camera first, until every probe of every window holds every sweep. A batch reads the caches as they
    // stood before the batch, so its probes' feedback does not depend on the order the pool runs them in.
    private void Trace(int workers) {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        var candidates = m_candidates;
        var results = m_results;

        while (true) {
            SdfMeshIrradianceScene scene;
            Lighting lighting;
            Layout layout;
            (int X, int Y, int Z)[] lows;
            Vector3 camera;
            int generation;

            lock (m_gate) {
                if (m_disposed || (m_scene is null) || (m_lighting is null)) { return; }
                scene = m_scene.Acquire();
                lighting = m_lighting;
                layout = m_layout;
                lows = Lows();
                camera = m_camera;
                generation = m_generation;
            }
            using var sceneBorrow = scene;

            candidates.Clear();
            for (var level = 0; (level < m_levels.Length); level++) {
                var cache = m_levels[level].Cache;
                var low = lows[level];
                var dims = layout.DimsOf(level: level);
                var spacing = layout.SpacingOf(level: level);

                for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                    for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                        for (var x = low.X; (x < (low.X + dims.X)); x++) {
                            var sweeps = ((cache.TryGetValue(key: (x, y, z), value: out var cached) && (cached.Current.Generation == generation)) ? cached.Current.Sweeps : 0);

                            if (sweeps >= layout.Sweeps) { continue; }

                            var center = (new Vector3(x: (x + 0.5f), y: (y + 0.5f), z: (z + 0.5f)) * spacing);

                            candidates.Add(item: (level, (x, y, z), sweeps, Vector3.DistanceSquared(value1: center, value2: camera)));
                        }
                    }
                }
            }
            if (candidates.Count == 0) {
                lock (m_gate) {
                    if ((generation != m_generation) || !SameLows(lows: lows) || (layout != m_layout)) { continue; }
                    m_owed = false;
                    if (!m_reported) {
                        var probes = 0;

                        foreach (var level in m_levels) { probes += level.Cache.Count; }
                        m_reported = true;
                        Console.Error.WriteLine(value: $"[sdf.mesh-gi] done probes={probes} traced={m_traced} rays={m_rays} depth-rays={m_depthRays} triangles={scene.TriangleCount} lights={lighting.Points.Length} sweeps={layout.Sweeps} seconds={m_clock.Elapsed.TotalSeconds:0.000}");
                    }
                }
                return;
            }

            candidates.Sort(comparison: static (a, b) => ((a.Sweeps != b.Sweeps) ? a.Sweeps.CompareTo(value: b.Sweeps) :
                ((a.Level != b.Level) ? a.Level.CompareTo(value: b.Level) : a.Distance.CompareTo(value: b.Distance))));

            var count = Math.Min(val1: BatchProbes, val2: candidates.Count);
            // A whole batch's block even for a short batch, so every block is large enough for the large object heap.
            var block = new ProbeBlock(count: BatchProbes);

            if (results.Length < count) {
                results = new ProbeData[BatchProbes];
                m_results = results;
            }

            _ = Parallel.For(fromInclusive: 0, toExclusive: count, parallelOptions: parallel, body: index => {
                var candidate = candidates[index];

                results[index] = TraceProbe(block: block, cell: candidate.Cell, generation: generation, index: index, layout: layout, level: candidate.Level, lighting: lighting, lows: lows, scene: scene, sweep: candidate.Sweeps);
            });

            m_landed.Clear();
            lock (m_gate) {
                if (generation != m_generation) { continue; }
                for (var index = 0; (index < count); index++) {
                    var (level, cell, _, _) = candidates[index];
                    var cache = m_levels[level].Cache;
                    var result = results[index];
                    var entry = new Probe(Current: result, HasPrevious: false, Previous: default);

                    // Bounce sweeps read the new generation, but viewers keep the previous complete measurement
                    // until this cell's replacement converges. Flatten the link if another edit interrupts it.
                    if (!result.Invalid && (result.Sweeps < layout.Sweeps) && cache.TryGetValue(key: cell, value: out var previous)) {
                        entry = ((previous.Current.Generation == generation)
                            ? (entry with { HasPrevious = previous.HasPrevious, Previous = previous.Previous })
                            : (entry with { HasPrevious = true, Previous = Readable(probe: previous) }));
                    }
                    m_depthRays += result.DepthRays;
                    cache[cell] = entry;
                    if (!entry.HasPrevious) { m_landed.Add(item: (level, cell)); }
                }
                m_traced += count;
                m_rays += (((long)count) * layout.Rays);
                Counters.Add(amount: count, kind: Probes);
            }
            Array.Clear(array: results, index: 0, length: count);
            StoreLanded();
        }
    }
    // Writes the landed probes into the volume, with no borrowing from neighbours. The trace composes them outside
    // the gate, from the cache, so the frame that
    // publishes the volume composes nothing, and writes them under it unless the window moved meanwhile, when it composes
    // the cells the moved window holds again.
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
                    m_restoreWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords)).CopyTo(destination: SlotWords(cell: cell, level: level));
                }
                m_landed.Clear();

                return;
            }
        }
    }
    // One sweep of one probe: its rays against the scene, each hit shaded under the captured lighting, the sweeps after
    // the first adding the light the hit reflects from cached probes; then which traced lights its origin sees.
    private ProbeData TraceProbe(int level, (int X, int Y, int Z) cell, int generation, Layout layout, Lighting lighting, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene, int sweep, ProbeBlock block, int index) {
        var spacing = layout.SpacingOf(level: level);
        var cache = m_levels[level].Cache;
        var centre = (new Vector3(x: (cell.X + 0.5f), y: (cell.Y + 0.5f), z: (cell.Z + 0.5f)) * spacing);
        bool valid;
        Vector3 offset;
        float inside;

        if ((sweep > 0) && cache.TryGetValue(key: cell, value: out var cached)) {
            // Every sweep stands where the first did, so one origin owns the probe's cubes, depth and light visibility.
            offset = cached.Current.Offset;
            valid = TraceRadiance(inside: out inside, layout: layout, lighting: lighting, lows: lows, origin: (centre + offset), radiance: block.Radiance(index: index),
                scene: scene, sky: block.Sky(index: index), sweep: sweep);
        } else {
            valid = Place(centre: centre, inside: out inside, layout: layout, lighting: lighting, lows: lows, offset: out offset, radiance: block.Radiance(index: index),
                scene: scene, sky: block.Sky(index: index), spacing: spacing);
        }

        var origin = (centre + offset);
        var lights = block.Lights(index: index);
        var depthMoments = block.Depth(index: index);
        var depthRays = 0;

        if (valid) {
            // A light past its range lights nothing here and stays unseen; one at the probe's origin is seen.
            foreach (var point in lighting.Points) {
                var toward = (point.Position - origin);
                var distance = toward.Length();

                if ((distance >= point.Range) || ((distance > LightBias) && !scene.Unblocked(direction: (toward / distance), maximum: (distance - LightBias), origin: origin))) { continue; }
                lights[(point.Index >> 5)] |= (1u << (point.Index & 31));
            }

        }

        if (valid) {
            if ((sweep > 0) && cache.TryGetValue(key: cell, value: out var previous)) {
                previous.Current.Depth.CopyTo(destination: depthMoments);
            } else {
                // The farthest in-window receiver is a spacing diagonal plus relocation and the normal bias.
                depthRays = SdfMeshProbeDepth.Trace(scene: scene, origin: origin,
                    reach: (((1.5f * MathF.Sqrt(x: 3f)) * spacing) + SdfMeshIrradianceProbe.NormalBias), surfaces: depthMoments);
            }
        }

        return new ProbeData(Block: block, DepthRays: depthRays, Generation: generation, Index: index, Inside: inside, Invalid: !valid, Offset: offset, Sweeps: (sweep + 1));
    }
    // Take the nearest clear exit, then try the cell's grid nearest-first. Openness scoring moved probes a metre
    // off thin floors and made neighbouring cells choose different storeys. Surface reconstruction handles grazing.
    private bool Place(Vector3 centre, float spacing, Layout layout, Lighting lighting, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene,
        Span<float> sky, Span<Vector3> radiance, out Vector3 offset, out float inside) {
        var clearance = SdfMeshIrradianceProbe.Clearance;
        var reach = (0.5f * spacing);

        offset = Vector3.Zero;

        var near = scene.TryNearestSurface(bakeHalfDiagonal: out _, normal: out var normal, point: centre, radius: clearance, surface: out var surface);
        var valid = TraceRadiance(inside: out inside, layout: layout, lighting: lighting, lows: lows, origin: centre, radiance: radiance, scene: scene, sky: sky, sweep: 0);

        if (valid && !near) { return true; }
        if (!near && !scene.TryNearestSurface(bakeHalfDiagonal: out _, normal: out normal, point: centre, radius: ((reach * MathF.Sqrt(x: 3f)) + clearance), surface: out surface)) { return false; }

        var pushed = ((surface + (normal * (1.25f * clearance))) - centre);

        for (var candidate = -1; (candidate < Candidates.Length); candidate++) {
            var trial = ((candidate < 0) ? pushed : (Candidates[candidate] * spacing));

            if ((MathF.Abs(x: trial.X) > reach) || (MathF.Abs(x: trial.Y) > reach) || (MathF.Abs(x: trial.Z) > reach)) { continue; }

            var origin = (centre + trial);

            if (scene.TryNearestSurface(bakeHalfDiagonal: out _, normal: out _, point: origin, radius: clearance, surface: out _)) { continue; }
            if (Blocked(origin: origin, reach: spacing, scene: scene)) { continue; }
            if (!TraceRadiance(inside: out inside, layout: layout, lighting: lighting, lows: lows, origin: origin, radiance: radiance, scene: scene, sky: sky, sweep: 0)) { continue; }

            offset = trial;
            return true;
        }

        // Nothing in the cell stands clear: the centre's own measurement is kept, as an invalid probe's.
        _ = TraceRadiance(inside: out inside, layout: layout, lighting: lighting, lows: lows, origin: centre, radiance: radiance, scene: scene, sky: sky, sweep: 0);
        return false;
    }
    // Reject a buried candidate with a few local rays before tracing its full radiance.
    private static bool Blocked(Vector3 origin, SdfMeshIrradianceScene scene, float reach) {
        const int Rays = 32;
        var backFaces = 0;

        for (var ray = 0; (ray < Rays); ray++) {
            var direction = PlacementDirections[ray];

            if (!scene.Intersect(direction: direction, distance: out _, material: out _, maximum: reach, normal: out var normal, origin: origin)) { continue; }
            if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) { backFaces++; }
        }

        return (backFaces > (InsideShare * Rays));
    }
    // The same hemispherical radiance measurement serves lattice probes and a camera in a gap between them. This
    // reads the existing scene and bounce cache; a camera sample is never written back into the irradiance volume.
    // `inside` receives the share of the rays that met back faces; the measurement is valid while it is at most
    // InsideShare. The cubes are written from zero.
    private bool TraceRadiance(Vector3 origin, Layout layout, Lighting lighting, (int X, int Y, int Z)[] lows,
        SdfMeshIrradianceScene scene, int sweep, Span<float> sky, Span<Vector3> radiance, out float inside) {
        Span<float> total = stackalloc float[6];
        var backFaces = 0;

        sky.Clear();
        radiance.Clear();
        var directions = ((layout.Rays == MediumRays) ? MediumDirections : HighDirections);

        for (var ray = 0; (ray < layout.Rays); ray++) {
            var direction = directions[ray];
            var hit = scene.Intersect(origin: origin, direction: direction, maximum: lighting.Reach, distance: out var distance, material: out var material, normal: out var normal);
            var light = Vector3.Zero;

            if (hit) {
                if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) {
                    backFaces++;
                } else {
                    light = Shade(layout: layout, lighting: lighting, lows: lows, material: material, normal: normal, point: (origin + (direction * distance)), scene: scene, sweep: sweep);
                }
            }
            for (var axis = 0; (axis < 6); axis++) {
                var weight = MathF.Max(x: 0f, y: Vector3.Dot(vector1: direction, vector2: Axes[axis]));

                if (weight <= 0f) { continue; }
                total[axis] += weight;
                if (!hit) { sky[axis] += weight; }
                radiance[axis] += (light * weight);
            }
        }
        for (var axis = 0; (axis < 6); axis++) {
            if (total[axis] <= 0f) { continue; }
            sky[axis] /= total[axis];
            radiance[axis] /= total[axis];
        }
        inside = (backFaces / ((float)layout.Rays));

        return (inside <= InsideShare);
    }
    // The radiance a hit sends back along its ray, in the views pass's units (albedo times the light a surface receives):
    // the directional lights' bounce under a traced shadow, the traced point and spot lights' under their windowed
    // falloff, cone and a traced shadow, its emission, and from the second sweep the reflected light of the probes about
    // it.
    private Vector3 Shade(Lighting lighting, Vector3 point, Vector3 normal, int material, int sweep, Layout layout, (int X, int Y, int Z)[] lows, SdfMeshIrradianceScene scene) {
        var surface = lighting.MaterialAt(index: material);
        var reflected = ((surface.Albedo * (1f - surface.Metal)) * (surface.Bleed ?? Vector3.One));
        var result = Vector3.Zero;
        var lifted = (point + (normal * 1.0e-3f));

        if (lighting.Direct && (reflected != Vector3.Zero)) {
            var direct = Vector3.Zero;

            foreach (var light in lighting.Lights) {
                var lambert = MathF.Max(x: 0f, y: ((Vector3.Dot(vector1: normal, vector2: light.Direction) + surface.Wrap) / (1f + surface.Wrap)));

                if (lambert <= 0f) { continue; }
                if (light.CastsShadow && !scene.Unblocked(direction: light.Direction, maximum: lighting.Reach, origin: lifted)) { continue; }
                direct += (light.Color * ((light.Weight * lambert) * light.Bounce));
            }
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
                if ((distance > LightBias) && !scene.Unblocked(direction: direction, maximum: (distance - LightBias), origin: lifted)) { continue; }
                direct += (light.Color * ((((light.Weight * SdfPhotometry.Falloff(distance: distance, range: light.Range)) * (cone * cone)) * lambert) * light.Bounce));
            }
            result += ((reflected * direct) * lighting.Gains.Lights);
        }
        // A lane-driven emissive reads its lane from a frame slot, and a static draw has none: it reads zero, as the views
        // pass shades it.
        if (lighting.Emission && (surface.Emissive > 0f) && (surface.EmissiveLane is null)) {
            result += (((surface.Albedo * surface.Emissive) * (surface.Bleed ?? Vector3.One)) * lighting.Gains.Emission);
        }
        if (lighting.Feedback && (sweep > 0) && (reflected != Vector3.Zero)) {
            result += ((reflected * Incoming(layout: layout, lighting: lighting, lows: lows, normal: normal, point: point)) * lighting.Gains.Feedback);
        }

        return result;
    }
    // The light a surface receives from the cached probes about it, as the views pass reads the volume
    // (sdfMeshProbesAt, shade/sdf-mesh-irradiance.hlsli): the probes Select chooses, each weighted as Gather weighs it.
    // It reads each probe's own cubes, exactly as the published volume does.
    private Vector3 Incoming(Lighting lighting, Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z)[] lows) {
        var squared = (normal * normal);
        var skyIrradiance = (lighting.Sky ? (lighting.SkyIrradiance(normal: normal) * lighting.Gains.Sky) : Vector3.Zero);
        Span<float> cornerWeights = stackalloc float[8];
        Span<(int X, int Y, int Z)> cornerCells = stackalloc (int X, int Y, int Z)[8];
        var cornerProbes = new Corners();

        _ = Select(cells: cornerCells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, layout: layout, lows: lows, normal: normal, point: point, readable: false);

        var sky = 0f;
        var bounce = Vector3.Zero;
        var total = 0f;

        for (var corner = 0; (corner < 8); corner++) {
            if (cornerWeights[corner] <= 0f) { continue; }

            var probe = cornerProbes[corner];

            sky += (Cube(cube: probe.Sky, normal: normal, squared: squared) * cornerWeights[corner]);
            bounce += (Cube(cube: probe.Radiance, normal: normal, squared: squared) * cornerWeights[corner]);
            total += cornerWeights[corner];
        }

        if (total <= 0f) {
            return Vector3.Zero;
        }

        return ((skyIrradiance * (sky / total)) + (bounce / total));
    }

    /// <summary>Reads the eight probes about a receiver as the views pass gathers them (<c>sdfMeshProbesAt</c>): each
    /// corner's cell and its weight after facing and depth visibility, zero for a corner that does not answer, at the
    /// level the views pass reads there. The lookup clamps to that level's window's boundary cells, the visibility tests
    /// the receiver pushed <see cref="SdfMeshIrradianceProbe.NormalBias"/> metres off its surface, and the probes read
    /// are the ones views read: a cell's last complete measurement.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <param name="cells">Receives the eight corners' cells, corner <c>c</c> at offset <c>(c &amp; 1, c &gt;&gt; 1 &amp; 1,
    /// c &gt;&gt; 2)</c> from the lookup's first cell; at least eight long.</param>
    /// <param name="weights">Receives the eight corners' weights; at least eight long.</param>
    /// <returns><see langword="false"/> while the volume has no settled published window to read.</returns>
    public bool TryGather(Vector3 point, Vector3 normal, Span<(int X, int Y, int Z)> cells, Span<float> weights) =>
        TryGather(cells: cells, normal: normal, point: point, reports: default, weights: weights);
    /// <summary>Reads the eight probes about a receiver as <see cref="TryGather(Vector3, Vector3, Span{ValueTuple{int, int, int}}, Span{float})"/>
    /// does, and reports each corner's record and the terms of its weight.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <param name="cells">Receives the eight corners' cells; at least eight long.</param>
    /// <param name="weights">Receives the eight corners' weights; at least eight long.</param>
    /// <param name="reports">Receives each corner's report; at least eight long.</param>
    /// <returns><see langword="false"/> while the volume has no settled published window to read.</returns>
    public bool TryGather(Vector3 point, Vector3 normal, Span<(int X, int Y, int Z)> cells, Span<float> weights, Span<SdfMeshProbeReport> reports) {
        lock (m_gate) {
            if ((m_lighting is null) || !m_windowValid || Settling) { return false; }
            var probes = new Corners();

            _ = Select(cells: cells, cornerProbes: ref probes, cornerWeights: weights, layout: m_layout, lows: Lows(), normal: normal, point: point, readable: true, reports: reports);

            return true;
        }
    }
    /// <summary>Reads a traced static light's visibility using the published gather, as the views shader does: zero where
    /// no measured probe answers.</summary>
    /// <param name="point">The receiver position.</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <param name="lightIndex">The lights-table index.</param>
    /// <returns>The weighted visible share.</returns>
    public float LightVisibility(Vector3 point, Vector3 normal, int lightIndex) {
        if (((uint)lightIndex) >= (32u * SdfMeshIrradianceProbe.LightWords)) { return 1f; }
        lock (m_gate) {
            Span<(int X, int Y, int Z)> cells = stackalloc (int, int, int)[8];
            Span<float> weights = stackalloc float[8];
            var probes = new Corners();

            var (_, measured) = Select(point: point, normal: normal, layout: m_layout, lows: Lows(), readable: true,
                cells: cells, cornerWeights: weights, cornerProbes: ref probes);

            if (!measured) { return 0f; }

            var visible = 0f;
            var total = 0f;

            for (var corner = 0; (corner < 8); corner++) {
                if (weights[corner] <= 0f) { continue; }
                if ((probes[corner].Lights[(lightIndex >> 5)] & (1u << (lightIndex & 31))) != 0u) { visible += weights[corner]; }
                total += weights[corner];
            }
            return ((total > 0f) ? (visible / total) : 0f);
        }
    }
    /// <summary>Describes the eight probes about a receiver as the views pass gathers them: the level it reads, whether
    /// a measured probe answers there, and that level's spacing and window, then one line a corner with its cell, where
    /// its probe stands (its centre plus the offset the trace moved it by), the share of its rays that met back faces,
    /// whether it is valid, its sweeps, and the terms of its weight at the receiver (trilinear share, facing, the depth
    /// visibility, whether it lies in front) with the weight it answers with.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <returns>The lines; one line while the volume holds no window to read.</returns>
    public IReadOnlyList<string> DescribeProbes(Vector3 point, Vector3 normal) {
        var cells = new (int X, int Y, int Z)[8];
        var weights = new float[8];
        var reports = new SdfMeshProbeReport[8];
        Layout layout;
        (int X, int Y, int Z) low;
        int level;
        bool measured;

        lock (m_gate) {
            if (Settling) {
                var live = m_levels[0].Low;

                return [string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[world.probe: unsettled; published-window=({m_published.Low.X}, {m_published.Low.Y}, {m_published.Low.Z})+{m_published.Dims.X}x{m_published.Dims.Y}x{m_published.Dims.Z} live-window=({live.X}, {live.Y}, {live.Z}); {Describe()}]")];
            }
            if ((m_lighting is null) || !m_windowValid) {
                return [$"[world.probe: no volume at ({point.X:0.##}, {point.Y:0.##}, {point.Z:0.##}); {Describe()}]"];
            }
            layout = m_layout;

            var probes = new Corners();

            (level, measured) = Select(cells: cells, cornerProbes: ref probes, cornerWeights: weights, layout: layout, lows: Lows(), normal: normal, point: point, readable: true, reports: reports);
            low = m_levels[level].Low;

            var lines = new List<string>(capacity: 9);
            var total = 0f;
            var answering = 0;

            foreach (var weight in weights) {
                if (weight > 0f) {
                    total += weight;
                    answering++;
                }
            }
            lines.Add(item: string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
                $"[world.probe: point=({point.X:0.###}, {point.Y:0.###}, {point.Z:0.###}) normal=({normal.X:0.##}, {normal.Y:0.##}, {normal.Z:0.##}) level={level} measured={(measured ? 1 : 0)} spacing={layout.SpacingOf(level: level):0.##} window=({low.X}, {low.Y}, {low.Z})+{layout.DimsOf(level: level).X}x{layout.DimsOf(level: level).Y}x{layout.DimsOf(level: level).Z} answering={answering} weight={total:0.####}]"));
            lines.Add(item: Describe());
            foreach (var report in reports) {
                var description = report.Describe();

                if (report.Present && !report.Invalid && (m_scene is { } scene)) {
                    var toward = ((point + (normal * SdfMeshIrradianceProbe.NormalBias)) - report.Position);
                    var distance = toward.Length();
                    var clear = ((distance <= 1e-3f) || scene.Unblocked(report.Position, (toward / distance), (distance - 1e-3f)));

                    description = description.Insert(startIndex: (description.Length - 1), value: $" reference-clear={(clear ? 1 : 0)}");
                }
                lines.Add(item: description);
            }

            return lines;
        }
    }

    // KEEP IN SYNC with sdfMeshProbesAt. Chooses the probes a receiver reads: those of the finest level whose window
    // holds it within its boundary probes, the coarsest past every window, extended as far as its boundary probes' surface
    // support admits. A coarser level never answers inside a finer window: its angular support is metres wide there, so
    // it would see through the thin wall a finer probe measured. When none of the level's measured probes answer, the
    // same probes answer past their measured reach, blocked only by the surfaces they measured, and Measured is false:
    // the receiver lies past what they measured, they are the best estimate of its light, reading none would draw a
    // surface the camera sees black, and a surface they saw between them and it still hides it.
    private (int Level, bool Measured) Select(Vector3 point, Vector3 normal, Layout layout, ReadOnlySpan<(int X, int Y, int Z)> lows, bool readable,
        Span<(int X, int Y, int Z)> cells, Span<float> cornerWeights, ref Corners cornerProbes, Span<SdfMeshProbeReport> reports = default) {
        var level = 0;

        while ((level < (m_levels.Length - 1)) && (Outside(layout: layout, level: level, low: lows[level], normal: normal, point: point) > 0f)) { level++; }
        Gather(cells: cells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, depth: true, layout: layout, level: level, low: lows[level],
            normal: normal, point: point, readable: readable, reports: reports);

        var total = 0f;

        foreach (var weight in cornerWeights[..8]) { total += weight; }
        if (total > 0f) { return (level, true); }
        Gather(cells: cells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, depth: false, layout: layout, level: level, low: lows[level],
            normal: normal, point: point, readable: readable, reports: reports);
        return (level, false);
    }
    // How far past a level's boundary probes a receiver stands, in cells, at most one; zero within them.
    private static float Outside(Layout layout, int level, (int X, int Y, int Z) low, Vector3 normal, Vector3 point) {
        var (unclamped, sample) = Lattice(layout: layout, level: level, low: low, normal: normal, point: point);

        return Math.Clamp(value: (unclamped - sample).Length(), min: 0f, max: 1f);
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
    // The eight probes of a level about a receiver: each corner's cell, probe and weight after facing and angular surface
    // visibility, bounded by each probe's measured reach when `depth`, zero where the corner does not answer. The bounce read gathers the probes the trace is
    // landing (Current); a viewer's read gathers the ones views read (Readable). A nonempty `reports` receives each
    // corner's record and the terms of its weight.
    private void Gather(int level, Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z) low, bool readable, bool depth, Span<(int X, int Y, int Z)> cells, Span<float> cornerWeights, ref Corners cornerProbes, Span<SdfMeshProbeReport> reports = default) {
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

        for (var corner = 0; (corner < 8); corner++) {
            var offset = new Vector3(x: corner & 1, y: (corner >> 1) & 1, z: (corner >> 2) & 1);
            var cell = (((int)(floor.X + offset.X)), ((int)(floor.Y + offset.Y)), ((int)(floor.Z + offset.Z)));

            cells[corner] = cell;
            cornerWeights[corner] = 0f;
            if (!reports.IsEmpty) { reports[corner] = new SdfMeshProbeReport(Cell: cell, Position: ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * spacing))); }
            if (
                (cell.Item1 < low.X) || (cell.Item1 >= (low.X + dims.X)) ||
                (cell.Item2 < low.Y) || (cell.Item2 >= (low.Y + dims.Y)) ||
                (cell.Item3 < low.Z) || (cell.Item3 >= (low.Z + dims.Z)) ||
                !cache.TryGetValue(key: cell, value: out var entry)
            ) {
                continue;
            }

            var probe = (readable ? Readable(probe: entry) : entry.Current);
            var weights = new Vector3(
                x: ((offset.X > 0f) ? fraction.X : (1f - fraction.X)),
                y: ((offset.Y > 0f) ? fraction.Y : (1f - fraction.Y)),
                z: ((offset.Z > 0f) ? fraction.Z : (1f - fraction.Z))
            );
            // KEEP IN SYNC with sdfMeshProbesAt: continuous positive-hemisphere weight, then depth visibility.
            var probePoint = ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * spacing) + probe.Offset);
            var toward = (probePoint - point);
            // A distance fade rejects probes behind the receiver without the angular pole that a close, relocated
            // probe's squared cosine stamped into flat floors. Irradiance is already evaluated along the normal.
            var facing = Math.Clamp((Vector3.Dot(vector1: toward, vector2: normal) / (0.5f * spacing)), 0f, 1f);

            facing = float.Lerp(amount: outside, value1: ((facing * facing) * (3f - (2f * facing))), value2: 1f);
            var trilinear = ((weights.X * weights.Y) * weights.Z);
            var visibility = (probe.Invalid ? 0f : SdfMeshProbeDepth.Visibility(bounded: depth, surfaces: probe.Depth, toward: (pushed - probePoint)));
            var weight = ((trilinear * facing) * visibility);
            var front = (facing > 0f);

            if (!reports.IsEmpty) {
                var sky = 0f;
                var luminance = 0f;

                for (var axis = 0; (axis < 6); axis++) {
                    sky += (probe.Sky[axis] / 6f);
                    luminance += (SdfPhotometry.LuminanceOf(color: probe.Radiance[axis]) / 6f);
                }
                reports[corner] = new SdfMeshProbeReport(Cell: cell, Facing: facing, Front: front, Inside: probe.Inside, Invalid: probe.Invalid, Luminance: luminance,
                    Offset: probe.Offset, Position: probePoint, Present: true, Sky: sky, Sweeps: probe.Sweeps, Trilinear: trilinear, Visibility: visibility);
            }
            if (probe.Invalid || (weight <= 0f)) { continue; }

            cornerProbes[corner] = probe;
            cornerWeights[corner] = weight;
        }

        if (!reports.IsEmpty) {
            for (var corner = 0; (corner < 8); corner++) {
                reports[corner] = (reports[corner] with { Weight = cornerWeights[corner] });
            }
        }
    }
    private static float Cube(ReadOnlySpan<float> cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));
    private static Vector3 Cube(ReadOnlySpan<Vector3> cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));

    private static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
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

    // One probe: its sky visibility and radiance cubes, its angular surface support, the traced lights its origin
    // sees (one bit a lights-table index), the sweeps
    // it holds, where it stands relative to its cell's centre (Offset, zero unless the trace moved it out of geometry or off
    // a face), the share of its rays that met back faces (Inside) and whether it lies inside geometry. Its cubes and surface support
    // are a slice of the floats of the batch it was traced in (ProbeBlock), its lights among them, so a batch allocates one
    // block rather than four arrays a probe, in the large object heap, where a collection of the young generations never
    // copies it.
    private readonly record struct ProbeData(ProbeBlock Block, int Index, int Sweeps, int Generation, bool Invalid, Vector3 Offset, float Inside, int DepthRays = 0) {
        public Span<float> Sky => Block.Sky(index: Index);
        public Span<Vector3> Radiance => Block.Radiance(index: Index);
        public Span<uint> Depth => Block.Depth(index: Index);
        public Span<uint> Lights => Block.Lights(index: Index);
    }
    // The words of one trace batch's probes, each probe's at its index: six sky, six radiance (18 floats) and the packed octahedral depth map, then its light words, held as the floats' bits so a batch is one allocation.
    private sealed class ProbeBlock(int count) {
        private const int DepthWords = SdfMeshIrradianceProbe.DepthWords;
        private const int FloatsPerProbe = ((24 + DepthWords) + SdfMeshIrradianceProbe.LightWords);

        private readonly float[] m_floats = new float[(count * FloatsPerProbe)];

        public long Bytes => (((long)m_floats.Length) * sizeof(float));

        public Span<float> Sky(int index) => m_floats.AsSpan(length: 6, start: (index * FloatsPerProbe));
        public Span<Vector3> Radiance(int index) => MemoryMarshal.Cast<float, Vector3>(span: m_floats.AsSpan(length: 18, start: ((index * FloatsPerProbe) + 6)));
        public Span<uint> Depth(int index) => MemoryMarshal.Cast<float, uint>(span: m_floats.AsSpan(length: DepthWords, start: ((index * FloatsPerProbe) + 24)));
        public Span<uint> Lights(int index) => MemoryMarshal.Cast<float, uint>(span: m_floats.AsSpan(length: SdfMeshIrradianceProbe.LightWords, start: (((index * FloatsPerProbe) + 24) + DepthWords)));
    }
    // A cell's entry in the cache: the probe the trace last landed and, while bounce sweeps of a new generation converge,
    // the previous complete measurement viewers keep reading (HasPrevious).
    private readonly record struct Probe(ProbeData Current, ProbeData Previous, bool HasPrevious);

    private static ProbeData Readable(Probe probe) => (probe.HasPrevious ? probe.Previous : probe.Current);

    // The probes by cell: an open-addressed table of entries, so a probe the trace lands costs no allocation and the walk
    // into an untraced area promotes no small objects. The trace writes it under the volume's gate, one writer at a time;
    // the frame, the refill and the trace's workers read it without the gate, each entry under a sequence lock (its version
    // is odd while a write is in progress), and a grown table is published whole, so a reader holding the old one reads a
    // consistent, slightly earlier cache.
    private sealed class ProbeCache {
        private const int InitialCapacity = (1 << 16);

        private Table? m_table;
        private int m_capacity = InitialCapacity;

        // The probes cached.
        public int Count => (Volatile.Read(location: ref m_table)?.Count ?? 0);

        // The bytes the cache holds: its table's arrays and every probe block an entry names, each block once. Read without
        // the gate, so a trace landing meanwhile can leave it a block off.
        public (long Table, long Blocks, int Capacity) Bytes() {
            if (Volatile.Read(location: ref m_table) is not { } table) { return (0, 0, 0); }

            var capacity = table.Keys.Length;
            var tableBytes = (((long)capacity) * ((((3 * sizeof(int)) + sizeof(int)) + sizeof(int)) + System.Runtime.CompilerServices.Unsafe.SizeOf<Probe>()));
            var blocks = new HashSet<ProbeBlock>(comparer: ReferenceEqualityComparer.Instance);
            var blockBytes = 0L;

            for (var slot = 0; (slot < capacity); slot++) {
                if (Volatile.Read(location: ref table.Used[slot]) == 0) { continue; }

                var entry = table.Entries[slot];

                if ((entry.Current.Block is { } current) && blocks.Add(item: current)) { blockBytes += current.Bytes; }
                if (entry.HasPrevious && (entry.Previous.Block is { } previous) && blocks.Add(item: previous)) { blockBytes += previous.Bytes; }
            }

            return (tableBytes, blockBytes, capacity);
        }
        // Forgets every probe; the next write starts a table of the capacity the cache last reached.
        public void Clear() {
            if (m_table is { } table) { m_capacity = table.Keys.Length; }
            Volatile.Write(location: ref m_table, value: null);
        }
        public bool ContainsKey((int X, int Y, int Z) key) => ((Volatile.Read(location: ref m_table) is { } table) && (Find(key: key, table: table) >= 0));
        public bool TryGetValue((int X, int Y, int Z) key, out Probe value) {
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
        public Probe this[(int X, int Y, int Z) key] {
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
            public readonly Probe[] Entries = new Probe[capacity];

            public int Count;
        }
    }
    // One level of the volume: its probes by cell, its window's first cell, the slots of its window still lacking a
    // measurement of their own cell, the slots composed since its last publication, and the window views read. Its
    // state is the volume's gate's, except the cache, which readers take under its own sequence locks.
    private sealed class Level(int index) {
        public int Index { get; } = index;
        public ProbeCache Cache { get; } = new();
        public List<int> Changed { get; } = [];

        public (int X, int Y, int Z) Low;
        public int Unwritten;
        public SdfMeshIrradianceWindow Published;
    }
    // Bounded by the frame's view count, not by how far the camera has travelled.
    private sealed record CameraSample((int X, int Y, int Z) Cell, int Generation, Vector3 Position, float[] Sky, Vector3[] Radiance, bool Settled, bool Invalid);
    // The eight probes about a point a read gathers, held without an allocation on the trace's hot path.
    [System.Runtime.CompilerServices.InlineArray(8)]
    private struct Corners {
        private ProbeData m_first;
    }
    // A static point or spot light the probes trace: its index in the lights table, and its record's position, colour,
    // intensity over pi, range, cone and bounce gain.
    private readonly record struct TracedLight(int Index, Vector3 Position, Vector3 Color, float Weight, float Range, Vector3 SpotAxis, float SpotOffset, float Bounce);
    // A tier's volume: its finest level's probe spacing and probes along each axis, rays a probe traces and sweeps (one
    // plus the bounces). The coarser levels take LevelSpacing and the Medium tier's window, whatever the tier, and their
    // slots follow the finer levels' in the volume.
    private readonly record struct Layout(float Spacing, (int X, int Y, int Z) Dims, int Rays, int Sweeps) {
        public float SpacingOf(int level) => ((level == 0) ? Spacing : LevelSpacing[(level - 1)]);
        public (int X, int Y, int Z) DimsOf(int level) => ((level == 0) ? Dims : MediumDims);
        public int CountOf(int level) {
            var dims = DimsOf(level: level);

            return ((dims.X * dims.Y) * dims.Z);
        }
        public int OffsetOf(int level) {
            var offset = 0;

            for (var finer = 0; (finer < level); finer++) { offset += CountOf(level: finer); }
            return offset;
        }

        public int TotalCount => OffsetOf(level: SdfMeshIrradianceProbe.Levels);

        public SdfMeshIrradianceWindow WindowOf(int level, (int X, int Y, int Z) low) =>
            new(Dims: DimsOf(level: level), Low: low, Spacing: SpacingOf(level: level));
        public static Layout Of(SdfIndirectTier tier, int? bounces) {
            var high = (tier == SdfIndirectTier.High);
            var cap = (high ? 3 : 2);

            return new Layout(
                Dims: (high ? HighDims : MediumDims),
                Rays: (high ? HighRays : MediumRays),
                Spacing: (high ? 1f : 2f),
                Sweeps: (1 + Math.Clamp(max: cap, min: 0, value: (bounces ?? cap)))
            );
        }
    }
    // The lighting a generation of probes traces under: the directional lights, the static point and spot lights, the
    // program's materials, the gains and sources, and the sky's irradiance projected on the CPU (SdfSkyEnvironment),
    // which the bounced light reads.
    private sealed class Lighting {
        public bool Direct { get; init; }
        public bool Emission { get; init; }
        public bool Feedback { get; init; }
        public required SdfIndirectGains Gains { get; init; }
        public required SdfLight[] Lights { get; init; }
        public required SdfMaterial[] Materials { get; init; }
        public required TracedLight[] Points { get; init; }
        public required float Reach { get; init; }
        public bool Sky { get; init; }
        public required Vector3[] SkyCoefficients { get; init; }
        public float SkyPeak { get; init; }

        public SdfMaterial MaterialAt(int index) =>
            (((index >= 0) && (index < Materials.Length)) ? Materials[index] : new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
        // The sky's cosine-weighted mean radiance about a normal: its irradiance over pi, the views pass's unit.
        public Vector3 SkyIrradiance(Vector3 normal) => (SdfSkyEnvironment.Irradiance(coefficients: SkyCoefficients, normal: normal) / MathF.PI);
        public static Lighting Capture(SdfFrame frame) {
            var lights = new List<SdfLight>();
            var points = new List<TracedLight>();
            var records = frame.Lights.Records;

            for (var index = 0; (index < frame.Lights.Count); index++) {
                var light = records[index];

                if ((light.Kind == SdfLightKind.Directional) && (light.Weight > 0f)) {
                    lights.Add(item: light with { Direction = SdfLights.UnitDirection(direction: light.Direction) });
                } else if (light.IsTraced && (light.Weight > 0f) && (light.Param > 0f)) {
                    points.Add(item: new TracedLight(Bounce: light.Bounce, Color: light.Color, Index: index, Position: light.Direction, Range: light.Param, SpotAxis: light.SpotAxis, SpotOffset: light.SpotOffset, Weight: light.Weight));
                }
            }

            var coefficients = new Vector3[SdfSkyEnvironment.CoefficientCount];
            var sources = frame.IndirectSources;
            var skyPeak = 0f;

            if ((sources & SdfIndirectSources.Sky) != 0) {
                var map = new Vector3[SdfSkyEnvironment.Texels];
                var layers = new SdfSkyLayer[SdfSky.MaxLayers];

                frame.Sky.Pack(frame.Lights, frame.FarDistance, new SdfSkyDetails(), out var block, layers);
                SdfSkyEnvironment.Render(block: in block, layers: layers, map: map);
                SdfSkyEnvironment.Project(coefficients: coefficients, map: map);
                foreach (var texel in map) {
                    skyPeak = MathF.Max(x: skyPeak, y: SdfPhotometry.LuminanceOf(color: texel));
                }
            }

            return new Lighting {
                SkyPeak = skyPeak,
                Direct = ((sources & SdfIndirectSources.Direct) != 0),
                Emission = ((sources & SdfIndirectSources.Emission) != 0),
                Feedback = ((sources & SdfIndirectSources.Feedback) != 0),
                Gains = frame.IndirectGains,
                Lights = [.. lights],
                Materials = [.. frame.Program.Materials],
                Points = [.. points],
                Reach = MathF.Max(x: 1f, y: frame.FarDistance),
                Sky = ((sources & SdfIndirectSources.Sky) != 0),
                SkyCoefficients = coefficients,
            };
        }
    }
}
