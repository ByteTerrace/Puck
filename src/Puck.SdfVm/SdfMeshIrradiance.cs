using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The pass-block parameters of the mesh path's irradiance volume (<see cref="SdfMeshIrradiance"/>).</summary>
/// <param name="Enabled">Whether views read the volume.</param>
/// <param name="Spacing">The distance between neighbouring probes, in metres.</param>
/// <param name="Dims">The probes along each axis of the volume.</param>
/// <param name="Low">The volume's first cell: a probe of cell <c>c</c> sits at <c>(c + 0.5) * Spacing</c>.</param>
public readonly record struct SdfMeshIrradianceParameters(bool Enabled, float Spacing, (int X, int Y, int Z) Dims, (int X, int Y, int Z) Low);
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
/// through the wall or slab between them, and the plane its ray toward each of its 26 neighbouring cells first meets,
/// so a surface past the wall or slab the probe's rays stop at in its direction takes nothing from it
/// (<see cref="PlaneVisibility"/>). A probe whose rays mostly meet back faces lies inside geometry and is stored
/// as invalid. The volume is toroidal: a cell's probe lives in the slot its coordinates name modulo the volume's size,
/// keyed by its cell, so the views pass rejects a slot holding another cell's probe, and a move of the camera rewrites
/// only the slots of cells it brings in.</para>
/// <para>A probe's rays are few beside the contrast of a sunlit patch, so the volume a view reads is each probe's cubes
/// averaged with those of the neighbouring probes its centre sees (<see cref="FilterWeights"/>): a neighbour behind a
/// wall or slab, or inside geometry, takes no part, so the average smooths the estimate without carrying light through
/// a wall. The light visibility bits are each probe's own.</para>
/// <para>Probes are traced on the thread pool, nearest the camera first and every probe's first sweep before any second,
/// and cached by cell, so a static world traces each cell once per lighting: a camera returning to a place reads its
/// cached probes at once. A change of the static draws rebuilds the scene from the one it replaces, building only the
/// geometry new to it, and retraces every probe; a change of the directional lights, the static point and spot lights,
/// the gains, the sources, the bounces or what the program's materials reflect and emit retraces them over the same
/// scene. Until a probe lands, the old one keeps drawing. The dynamic draws (moving bodies) receive the volume's light
/// and do not cast into it; an anchored light, which moves, is neither traced nor occluded.</para>
/// </summary>
public sealed class SdfMeshIrradiance {
    /// <summary>The most probes a volume holds, the High tier's.</summary>
    public const int MaxProbes = ((48 * 16) * 48);
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

    /// <summary>The weight each of the 26 neighbouring probes takes in a probe's filtered cubes, beside the probe's own
    /// weight of one: one half across a face, a quarter across an edge and an eighth across a corner, so a probe whose
    /// centre sees every neighbour averages eight probes' worth of rays.</summary>
    public static readonly float[] FilterWeights = BuildFilterWeights();

    // The 26 neighbours' cell offsets, in an order whose reverse is 25 minus the index: offset n is the negation of
    // offset 25 - n, so a probe's visibility of a neighbour and the neighbour's of it are read from one mask each.
    private static readonly (int X, int Y, int Z)[] NeighbourOffsets = BuildNeighbourOffsets();
    private readonly Lock m_gate = new();
    private readonly ProbeCache m_cache = new();
    // The trace's candidates and a batch's results, kept across batches and traces: one trace runs at a time.
    private readonly List<((int X, int Y, int Z) Cell, int Sweeps, float Distance)> m_candidates = [];
    private ProbeData[] m_results = [];
    // The trace's store of a batch's landed probes (StoreLanded): the cells that landed, the window cells they restore,
    // and the slot words composed for those.
    private readonly List<(int X, int Y, int Z)> m_landed = [];
    private readonly HashSet<(int X, int Y, int Z)> m_restore = [];
    private readonly List<(int X, int Y, int Z)> m_restoreCells = [];
    private uint[] m_restoreWords = [];
    private readonly uint[] m_volume = new uint[(MaxProbes * SdfMeshIrradianceProbe.ProbeWords)];
    private readonly Stopwatch m_clock = new();

    private SdfWorldTables? m_writeTables;
    private Action<ReadOnlyMemory<uint>>? m_write;
    private SdfMeshIrradianceScene? m_scene;

    /// <summary>Returns the existing opaque static BVH only when it represents the supplied frame's exact draw inputs.</summary>
    /// <param name="frame">The retained rendered frame.</param>
    /// <returns>The matching scene, or null while absent or building.</returns>
    public SdfMeshIrradianceScene? DiagnosticScene(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        lock (m_gate) {
            var index = 0;

            foreach (var draw in frame.MeshDraws) {
                if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
                if ((index >= m_diagnosticDraws.Length) || (draw != m_diagnosticDraws[index++])) { return null; }
            }
            return ((index == m_diagnosticDraws.Length) ? m_scene : null);
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
    private int m_unwritten;
    // The window's refill (Refill): the window's version, which a move advances, whether its refill is owed, the refill in
    // flight on the thread pool and whether it still runs, and the cells it lists and the slot words it composes for them.
    private int m_windowVersion;
    private bool m_refillOwed;
    private bool m_refillRunning;
    private Task? m_refill;

    private readonly List<(int X, int Y, int Z)> m_refillCells = [];
    private uint[] m_refillWords = [];
    private CameraSample?[] m_cameraSamples = [];

    private int m_nextCameraSample;
    private (int X, int Y, int Z) m_low;
    private Vector3 m_camera;
    private bool m_windowValid;
    private Task? m_worker;
    // Whether the window may hold probes the trace has not yet traced: set when the scene, the lighting or the window
    // changes, cleared when a trace finds every probe of the window traced.
    private bool m_owed;
    private bool m_dirty;
    // Whether the latest Stage wrote the volume table: the frame it prepared must render even once the trace is done.
    private bool m_wrote;
    private bool m_reported;
    private long m_traced;
    private long m_rays;

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
                return ((m_worker is { IsCompleted: false } or { IsFaulted: true }) || (m_build is { IsCompleted: false }) || ((m_scene is not null) && m_owed) || m_refillOwed || m_dirty || m_wrote);
            }
        }
    }

    /// <summary>Advances the volume for a frame of the mesh path before the frame's upload: rebuilds the scene once the
    /// mesh bake has settled and the static draws changed, restarts the trace on a lighting change, moves the volume with
    /// the first view's camera, starts the trace of the probes the volume lacks, and writes the probes that landed into the
    /// tables' volume table.</summary>
    /// <param name="frame">The frame, its baked draws dressed in.</param>
    /// <param name="tables">The residency's tables.</param>
    /// <param name="tier">The indirect tier the views asked for; Off reads no volume.</param>
    /// <param name="bakePending">Whether the mesh bake still owes opaque static meshes, when the scene waits for it.</param>
    /// <returns>The parameters the views pass reads.</returns>
    public SdfMeshIrradianceParameters Stage(SdfFrame frame, SdfWorldTables tables, SdfIndirectTier tier, bool bakePending) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        // The writer is one delegate to a method of this instance, so a steady frame allocates nothing here.
        m_writeTables = tables;
        m_write ??= WriteToTables;

        return Stage(bakePending: bakePending, frame: frame, tier: tier, write: m_write);
    }

    private void WriteToTables(ReadOnlyMemory<uint> words) => m_writeTables!.WriteMeshIrradiance(words: words.Span);
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
    /// <param name="bakePending">Whether the mesh bake still owes opaque static meshes, when the scene waits for it.</param>
    /// <param name="write">Receives the volume's probe slots, <see cref="SdfMeshIrradianceProbe.ProbeWords"/> words each,
    /// whenever a frame changes them.</param>
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
            if (!bakePending) { _ = RefreshScene(frame: frame); }
            RefreshLighting(frame: frame, layout: layout);
            m_camera = WindowCamera(frame: frame);
            if (m_cameraSamples.Length != frame.Views.Count) {
                m_cameraSamples = new CameraSample?[frame.Views.Count];
                m_nextCameraSample = 0;
            }

            var cameraCell = (
                X: ((int)MathF.Floor(x: (m_camera.X / layout.Spacing))),
                Y: ((int)MathF.Floor(x: (m_camera.Y / layout.Spacing))),
                Z: ((int)MathF.Floor(x: (m_camera.Z / layout.Spacing)))
            );
            var low = (
                X: (cameraCell.X - (layout.Dims.X / 2)),
                Y: (cameraCell.Y - (layout.Dims.Y / 2)),
                Z: (cameraCell.Z - (layout.Dims.Z / 2))
            );
            var resizing = (!m_windowValid || (layout.Dims != m_layout.Dims) || (layout.Spacing != m_layout.Spacing));

            // The window recentres only once the camera leaves its middle, an eighth of it short of each face, so a camera
            // that moves back and forth across a cell (a body falling through a floor painting and out of the ceiling's)
            // keeps one window rather than moving it, refilling it and tracing its new probes every frame.
            if (!resizing && InMiddle(cell: cameraCell, dims: layout.Dims)) {
                low = m_low;
            }

            if (resizing || (low != m_low)) {
                var resized = resizing;

                m_low = low;
                m_layout = layout;
                m_windowValid = true;
                m_owed = true;
                if (resized) {
                    Array.Clear(array: m_volume);
                    m_cache.Clear();
                    m_generation++;
                    m_reported = false;
                }
                OweRefill(pooled: !frame.TickLocked);
            }
            m_layout = layout;
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
            // A new window keeps the previous GPU table and its coordinates until every replacement slot has a
            // measured sample. Publishing an empty slot would invent darkness; filling it with sky invents a leak.
            if (m_dirty && (m_unwritten == 0)) {
                write(obj: m_volume.AsMemory(length: (layout.Count * SdfMeshIrradianceProbe.ProbeWords), start: 0));
                m_dirty = false;
                m_wrote = true;
                m_published = new SdfMeshIrradianceParameters(Dims: layout.Dims, Enabled: true, Low: m_low, Spacing: layout.Spacing);
            }
            return (m_published.Enabled ? m_published : new SdfMeshIrradianceParameters(Dims: layout.Dims, Enabled: true, Low: m_low, Spacing: layout.Spacing));
        }
    }
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
    /// <returns><see langword="false"/> while no scene is available or the camera lies inside opaque geometry.</returns>
    public bool TryMeter(Vector3 position, Vector3 forward, out float luminance, out float skyVisibility, out SdfExposureMeterSource source) {
        luminance = 0f;
        skyVisibility = 1f;
        source = SdfExposureMeterSource.None;

        lock (m_gate) {
            if ((m_lighting is not { } lighting) || !m_windowValid) {
                return false;
            }

            var spacing = m_layout.Spacing;
            var sample = ((position / spacing) - new Vector3(value: 0.5f));
            var floor = new Vector3(x: MathF.Floor(x: sample.X), y: MathF.Floor(x: sample.Y), z: MathF.Floor(x: sample.Z));
            var fraction = (sample - floor);
            Span<float> axes = stackalloc float[6];
            var open = 0f;
            var total = 0f;

            for (var corner = 0; (corner < 8); corner++) {
                var offset = new Vector3(x: corner & 1, y: (corner >> 1) & 1, z: (corner >> 2) & 1);
                var cell = (((int)(floor.X + offset.X)), ((int)(floor.Y + offset.Y)), ((int)(floor.Z + offset.Z)));

                if (!m_cache.TryGetValue(key: cell, value: out var cached)) {
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

                var sampleAtCamera = CameraRadiance(lighting: lighting, position: position, scene: scene);

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

    private CameraSample CameraRadiance(Vector3 position, SdfMeshIrradianceScene scene, Lighting lighting) {
        var cell = (((int)MathF.Floor(x: (position.X / m_layout.Spacing))), ((int)MathF.Floor(x: (position.Y / m_layout.Spacing))), ((int)MathF.Floor(x: (position.Z / m_layout.Spacing))));

        foreach (var sample in m_cameraSamples) {
            if ((sample is null) || (sample.Generation != m_generation) || (sample.Cell != cell) || (sample.Settled != !m_owed)) { continue; }
            if (sample.Position == position) { return sample; }
            if (sample.Invalid) { continue; }

            var toward = (position - sample.Position);
            var distance = toward.Length();

            if (scene.Unblocked(direction: (toward / distance), maximum: distance, origin: sample.Position)) { return sample; }
        }

        // A cell can straddle a partition. A sample belongs to its actual origin, never an assumed open cell centre.
        var sky = new float[6];
        var radiance = new Vector3[6];
        var valid = TraceRadiance(origin: position, rotation: Quaternion.Identity, layout: m_layout, lighting: lighting,
            low: m_low, scene: scene, sweep: (m_layout.Sweeps - 1), sky: sky, radiance: radiance, inside: out _);
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
            return $"[sdf.mesh-gi] spacing={m_layout.Spacing:0.##} dims={m_layout.Dims.X}x{m_layout.Dims.Y}x{m_layout.Dims.Z} rays={m_layout.Rays} sweeps={m_layout.Sweeps} triangles={(m_scene?.TriangleCount ?? 0)} lights={(m_lighting?.Points.Length ?? 0)} cached={m_cache.Count} traced={m_traced} traced-rays={m_rays}";
        }
    }

    // Rebuilds the scene on the thread pool when the static draws changed, from the scene it replaces so only geometry new
    // to it builds; the old scene traces until the new one lands.
    // Returns the build to wait for before this frame can use its scene, or null when it is already installed.
    private Task? RefreshScene(SdfFrame frame) {
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

        return StartSceneBuild(draws: draws, frame: frame, key: key);
    }
    // Apart from RefreshScene, since the build's closure is made on entry to the method holding it, and nearly every frame
    // matches the installed scene.
    private Task StartSceneBuild(SdfFrame frame, int key, int draws) {
        var snapshot = frame.MeshDraws.ToArray();
        var previous = m_scene;

        m_buildingKey = key;
        m_build = Task.Run(action: () => {
            var clock = Stopwatch.StartNew();
            var scene = SdfMeshIrradianceScene.Build(draws: snapshot, previous: previous);

            Counters.Count(kind: Scenes);
            Counters.Add(amount: (scene?.GeometriesBuilt ?? 0), kind: GeometriesBuilt);
            Counters.Add(amount: (scene?.GeometriesReused ?? 0), kind: GeometriesReused);
            Counters.Add(amount: (scene?.TrianglesBuilt ?? 0L), kind: TrianglesBuilt);
            lock (m_gate) {
                m_scene = scene;
                m_diagnosticDraws = snapshot.Where(predicate: d => SdfMeshIrradianceScene.IsStatic(draw: d)).ToArray();
                m_sceneKey = key;
                m_buildingKey = 0;
                m_generation++;
                m_owed = true;
                m_reported = false;
                m_clock.Restart();
                m_traced = 0;
                m_rays = 0;
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
    // Whether a cell lies in the current window's middle: an eighth of the window, at least one cell, short of each face.
    private bool InMiddle((int X, int Y, int Z) cell, (int X, int Y, int Z) dims) =>
        (Middle(cell: cell.X, dims: dims.X, low: m_low.X) && Middle(cell: cell.Y, dims: dims.Y, low: m_low.Y) && Middle(cell: cell.Z, dims: dims.Z, low: m_low.Z));
    private static bool Middle(int cell, int low, int dims) {
        var margin = Math.Max(val1: 1, val2: (dims / 8));

        return ((cell >= (low + margin)) && (cell < ((low + dims) - margin)));
    }
    private bool InWindow((int X, int Y, int Z) cell) =>
        ((cell.X >= m_low.X) && (cell.X < (m_low.X + m_layout.Dims.X)) && (cell.Y >= m_low.Y) && (cell.Y < (m_low.Y + m_layout.Dims.Y)) &&
        (cell.Z >= m_low.Z) && (cell.Z < (m_low.Z + m_layout.Dims.Z)));
    // Owes the refill of a window the volume just moved to: no table is published until it lands, and it runs on the thread
    // pool (Refill), or in a lockstep frame on the frame's thread, since a window that jumps far re-stores every slot.
    private void OweRefill(bool pooled) {
        m_windowVersion++;
        m_refillOwed = true;
        m_unwritten = m_layout.Count;
        if (pooled && !m_refillRunning) {
            m_refillRunning = true;
            m_refill = Task.Run(action: Refill);
        }
    }
    // Writes every cached probe of the window whose slot holds another cell's: the cells are listed under the gate, their
    // slots composed outside it, and written under it again unless the window moved meanwhile, when the next round lists
    // the new window. A probe that lands in between stores itself and is left as it stands.
    private void Refill() {
        while (true) {
            int version;
            (int X, int Y, int Z) low;
            (int X, int Y, int Z) dims;

            lock (m_gate) {
                if (!m_refillOwed) {
                    m_refillRunning = false;
                    return;
                }

                version = m_windowVersion;
                low = m_low;
                dims = m_layout.Dims;
                m_refillCells.Clear();

                var unwritten = m_layout.Count;

                for (var z = low.Z; (z < (low.Z + dims.Z)); z++) {
                    for (var y = low.Y; (y < (low.Y + dims.Y)); y++) {
                        for (var x = low.X; (x < (low.X + dims.X)); x++) {
                            if (Holds(cell: (x, y, z), dims: dims)) {
                                unwritten--;
                                continue;
                            }
                            if (m_cache.ContainsKey(key: (x, y, z))) { m_refillCells.Add(item: (x, y, z)); }
                        }
                    }
                }

                m_unwritten = unwritten;
            }

            var cells = m_refillCells.Count;

            if (m_refillWords.Length < (cells * SdfMeshIrradianceProbe.ProbeWords)) {
                m_refillWords = new uint[(cells * SdfMeshIrradianceProbe.ProbeWords)];
            }

            var composed = 0;

            for (var index = 0; (index < cells); index++) {
                var cell = m_refillCells[index];

                if (Compose(cell: cell, words: m_refillWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (composed * SdfMeshIrradianceProbe.ProbeWords)))) {
                    m_refillCells[composed++] = cell;
                }
            }

            lock (m_gate) {
                if (version != m_windowVersion) { continue; }

                for (var index = 0; (index < composed); index++) {
                    var cell = m_refillCells[index];

                    if (Holds(cell: cell, dims: dims)) { continue; }
                    m_refillWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords))
                        .CopyTo(destination: SlotWords(cell: cell));
                    m_unwritten--;
                    m_dirty = true;
                }

                m_refillOwed = false;
                m_refillRunning = false;

                return;
            }
        }
    }
    // Whether a cell's slot holds that cell's probe.
    private bool Holds((int X, int Y, int Z) cell, (int X, int Y, int Z) dims) =>
        ((m_volume[(SlotOf(dims: dims, x: cell.X, y: cell.Y, z: cell.Z) * SdfMeshIrradianceProbe.ProbeWords)] & ~SdfMeshIrradianceProbe.InvalidFlag) == (KeyOf(x: cell.X, y: cell.Y, z: cell.Z) | SdfMeshIrradianceProbe.WrittenFlag));
    // A cell's slot words in the volume.
    private Span<uint> SlotWords((int X, int Y, int Z) cell) =>
        m_volume.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (SlotOf(x: cell.X, y: cell.Y, z: cell.Z, dims: m_layout.Dims) * SdfMeshIrradianceProbe.ProbeWords));
    // Composes a cached cell's slot words: its cubes averaged with the neighbours its centre sees, its own light visibility;
    // false, writing nothing, when the cache holds no probe of it. It reads the cache alone, so it runs outside the gate.
    private bool Compose((int X, int Y, int Z) cell, Span<uint> words) {
        if (!m_cache.TryGetValue(key: cell, value: out var cached)) { return false; }

        var probe = Readable(probe: cached);
        Span<float> sky = stackalloc float[6];
        Span<Vector3> radiance = stackalloc Vector3[6];

        Filter(cell: cell, probe: probe, radiance: radiance, sky: sky);
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
        words[SdfMeshIrradianceProbe.NeighboursWord] = probe.Neighbours;
        // KEEP IN SYNC with sdfMeshProbeOffset.
        words[SdfMeshIrradianceProbe.OffsetWord] = Pack(high: probe.Offset.Y, low: probe.Offset.X);
        words[(SdfMeshIrradianceProbe.OffsetWord + 1)] = Pack(high: probe.Inside, low: probe.Offset.Z);
        for (var index = 0; (index < probe.Planes.Length); index++) {
            var plane = probe.Planes[index];

            words[(SdfMeshIrradianceProbe.PlanesWord + (2 * index))] = Pack(high: plane.Y, low: plane.X);
            words[((SdfMeshIrradianceProbe.PlanesWord + (2 * index)) + 1)] = Pack(high: plane.W, low: plane.Z);
        }

        return true;
    }

    /// <summary>Returns how far a probe lets a point take its light: the first-hit planes of the probe's rays in the four
    /// lattice directions about the point's direction (the 3×3 directions of the cube face the point lies toward, blended
    /// bilinearly by where the point falls between them), each admitting the point while it lies on the probe's side of
    /// the plane and fading it to nothing over <see cref="SdfMeshIrradianceProbe.DepthFade"/> metres past it. A wall or
    /// slab the rays stop at is that plane, so a point past it takes nothing whichever way it lies from the probe; a
    /// finite blocker darkens only the directions whose rays meet it, and softly between them. A zero plane (a ray that
    /// reached open space) admits everything. KEEP IN SYNC with <c>sdfMeshProbePlaneVisibility</c>.</summary>
    /// <param name="planes">The probe's 26 first-hit planes (normal and distance, the normal facing away from the probe),
    /// in <see cref="NeighbourIndex"/> order.</param>
    /// <param name="toward">The point, relative to the probe's centre, in metres.</param>
    /// <param name="spacing">The distance between neighbouring probes, in metres.</param>
    /// <param name="suspect">The share of the admission the probe's rays did not see for themselves, from zero to one: a
    /// ray that reached open space, or whose hit plane lies more than a quarter of <paramref name="spacing"/> beyond
    /// the point (fully suspect at three quarters), admits the point only for want of a hit. Distance is perpendicular
    /// to the plane, so rays meeting the point's own surface agree even at oblique or grazing angles.</param>
    /// <returns>The visibility, from zero (hidden) to one (open).</returns>
    public static float PlaneVisibility(ReadOnlySpan<Vector4> planes, Vector3 toward, float spacing, out float suspect) {
        var size = Vector3.Abs(value: toward);
        var major = MathF.Max(x: size.X, y: MathF.Max(x: size.Y, y: size.Z));

        suspect = 0f;
        if (!(major > 0f)) { return 1f; }

        // The face is the major component's axis; the point's direction is arranged as (major, minor, minor).
        var xMajor = ((size.X >= size.Y) && (size.X >= size.Z));
        var yMajor = (!xMajor && (size.Y >= size.Z));
        var arranged = ((xMajor ? toward : (yMajor ? new Vector3(x: toward.Y, y: toward.X, z: toward.Z) : new Vector3(x: toward.Z, y: toward.X, z: toward.Y))) / major);
        var facing = ((arranged.X < 0f) ? -1 : 1);
        // The minor components, from -1 to 1, fall between the face's three texels at -1, 0 and 1.
        var minorY = (arranged.Y + 1f);
        var minorZ = (arranged.Z + 1f);
        var cellY = MathF.Min(x: MathF.Floor(x: minorY), y: 1f);
        var cellZ = MathF.Min(x: MathF.Floor(x: minorZ), y: 1f);
        var fractionY = (minorY - cellY);
        var fractionZ = (minorZ - cellZ);
        var visibility = 0f;

        for (var corner = 0; (corner < 4); corner++) {
            var highY = corner & 1;
            var highZ = (corner >> 1);
            var weight = (((highY != 0) ? fractionY : (1f - fractionY)) * ((highZ != 0) ? fractionZ : (1f - fractionZ)));
            var texelY = ((((int)cellY) + highY) - 1);
            var texelZ = ((((int)cellZ) + highZ) - 1);

            var (x, y, z) = (xMajor ? (facing, texelY, texelZ) : (yMajor ? (texelY, facing, texelZ) : (texelY, texelZ, facing)));
            var plane = planes[NeighbourIndex(x: x, y: y, z: z)];
            var normal = new Vector3(x: plane.X, y: plane.Y, z: plane.Z);
            var past = (Vector3.Dot(vector1: normal, vector2: toward) - plane.W);
            var admits = (weight * (1f - Math.Clamp(max: 1f, min: 0f, value: (past / SdfMeshIrradianceProbe.DepthFade))));
            var gap = ((plane != Vector4.Zero) ? -past : 1.0e30f);

            visibility += admits;
            suspect += (admits * Math.Clamp(max: 1f, min: 0f, value: ((gap - (0.25f * spacing)) / (0.5f * spacing))));
        }
        suspect = ((visibility > 0f) ? (suspect / visibility) : 0f);
        return visibility;
    }

    // A probe's cubes averaged with those of the cached, valid neighbours it and they see each other across, by the
    // filter weights; an invalid probe keeps its own.
    private void Filter((int X, int Y, int Z) cell, ProbeData probe, Span<float> sky, Span<Vector3> radiance) {
        var total = 1f;

        for (var axis = 0; (axis < 6); axis++) {
            sky[axis] = probe.Sky[axis];
            radiance[axis] = probe.Radiance[axis];
        }
        if (probe.Invalid) { return; }

        for (var index = 0; (index < NeighbourOffsets.Length); index++) {
            if ((probe.Neighbours & (1u << index)) == 0u) { continue; }

            var offset = NeighbourOffsets[index];

            if (!m_cache.TryGetValue(key: ((cell.X + offset.X), (cell.Y + offset.Y), (cell.Z + offset.Z)), value: out var cached)) { continue; }
            var neighbour = Readable(probe: cached);

            if (neighbour.Invalid) { continue; }
            if ((neighbour.Neighbours & (1u << (25 - index))) == 0u) { continue; }

            var weight = FilterWeights[index];

            for (var axis = 0; (axis < 6); axis++) {
                sky[axis] += (neighbour.Sky[axis] * weight);
                radiance[axis] += (neighbour.Radiance[axis] * weight);
            }
            total += weight;
        }
        for (var axis = 0; (axis < 6); axis++) {
            sky[axis] /= total;
            radiance[axis] /= total;
        }
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

    // The trace loop: batches of the window's probes that lack a sweep, fewest sweeps first and nearest the camera
    // first, until every probe of the window holds every sweep. A batch reads the cache as it stood before the batch, so
    // its probes' feedback does not depend on the order the pool runs them in.
    private void Trace(int workers) {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        var candidates = m_candidates;
        var results = m_results;

        while (true) {
            SdfMeshIrradianceScene scene;
            Lighting lighting;
            Layout layout;
            (int X, int Y, int Z) low;
            Vector3 camera;
            int generation;

            lock (m_gate) {
                if ((m_scene is null) || (m_lighting is null)) { return; }
                scene = m_scene;
                lighting = m_lighting;
                layout = m_layout;
                low = m_low;
                camera = m_camera;
                generation = m_generation;
            }

            candidates.Clear();
            for (var z = low.Z; (z < (low.Z + layout.Dims.Z)); z++) {
                for (var y = low.Y; (y < (low.Y + layout.Dims.Y)); y++) {
                    for (var x = low.X; (x < (low.X + layout.Dims.X)); x++) {
                        var sweeps = ((m_cache.TryGetValue(key: (x, y, z), value: out var cached) && (cached.Current.Generation == generation)) ? cached.Current.Sweeps : 0);

                        if (sweeps >= layout.Sweeps) { continue; }

                        var center = (new Vector3(x: (x + 0.5f), y: (y + 0.5f), z: (z + 0.5f)) * layout.Spacing);

                        candidates.Add(item: ((x, y, z), sweeps, Vector3.DistanceSquared(value1: center, value2: camera)));
                    }
                }
            }
            if (candidates.Count == 0) {
                lock (m_gate) {
                    if ((generation != m_generation) || (low != m_low) || (layout != m_layout)) { continue; }
                    m_owed = false;
                    if (!m_reported) {
                        m_reported = true;
                        Console.Error.WriteLine(value: $"[sdf.mesh-gi] done probes={m_cache.Count} traced={m_traced} rays={m_rays} triangles={scene.TriangleCount} lights={lighting.Points.Length} sweeps={layout.Sweeps} seconds={m_clock.Elapsed.TotalSeconds:0.000}");
                    }
                }
                return;
            }

            candidates.Sort(comparison: static (a, b) => ((a.Sweeps != b.Sweeps) ? a.Sweeps.CompareTo(value: b.Sweeps) : a.Distance.CompareTo(value: b.Distance)));

            var count = Math.Min(val1: BatchProbes, val2: candidates.Count);
            // A whole batch's block even for a short batch, so every block is large enough for the large object heap.
            var block = new ProbeBlock(count: BatchProbes);

            if (results.Length < count) {
                results = new ProbeData[BatchProbes];
                m_results = results;
            }

            _ = Parallel.For(fromInclusive: 0, toExclusive: count, parallelOptions: parallel, body: index => {
                var candidate = candidates[index];

                results[index] = TraceProbe(block: block, cell: candidate.Cell, generation: generation, index: index, layout: layout, lighting: lighting, low: low, scene: scene, sweep: candidate.Sweeps);
            });

            m_landed.Clear();
            lock (m_gate) {
                if (generation != m_generation) { continue; }
                for (var index = 0; (index < count); index++) {
                    var cell = candidates[index].Cell;
                    var result = results[index];
                    var entry = new Probe(Current: result, HasPrevious: false, Previous: default);

                    // Bounce sweeps read the new generation, but viewers keep the previous complete measurement
                    // until this cell's replacement converges. Flatten the link if another edit interrupts it.
                    if (!result.Invalid && (result.Sweeps < layout.Sweeps) && m_cache.TryGetValue(key: cell, value: out var previous)) {
                        entry = ((previous.Current.Generation == generation)
                            ? (entry with { HasPrevious = previous.HasPrevious, Previous = previous.Previous })
                            : (entry with { HasPrevious = true, Previous = Readable(probe: previous) }));
                    }
                    m_cache[cell] = entry;
                    if (!entry.HasPrevious) { m_landed.Add(item: cell); }
                }
                m_traced += count;
                m_rays += (((long)count) * layout.Rays);
                Counters.Add(amount: count, kind: Probes);
            }
            Array.Clear(array: results, index: 0, length: count);
            StoreLanded();
        }
    }
    // Writes the slots the batch's landed probes change into the volume: a landed probe changes its own filtered cubes and
    // its neighbours', which averaged it in. The trace composes them outside the gate, from the cache, so the frame that
    // publishes the volume composes nothing, and writes them under it unless the window moved meanwhile, when it composes
    // the cells the moved window holds again.
    private void StoreLanded() {
        if (m_landed.Count == 0) { return; }

        while (true) {
            int version;

            m_restore.Clear();
            m_restoreCells.Clear();
            lock (m_gate) {
                version = m_windowVersion;
                foreach (var landed in m_landed) {
                    _ = m_restore.Add(item: landed);
                    foreach (var offset in NeighbourOffsets) {
                        _ = m_restore.Add(item: ((landed.X + offset.X), (landed.Y + offset.Y), (landed.Z + offset.Z)));
                    }
                }
                foreach (var cell in m_restore) {
                    if (InWindow(cell: cell) && m_cache.ContainsKey(key: cell)) { m_restoreCells.Add(item: cell); }
                }
            }

            var cells = m_restoreCells.Count;

            if (m_restoreWords.Length < (cells * SdfMeshIrradianceProbe.ProbeWords)) {
                m_restoreWords = new uint[(cells * SdfMeshIrradianceProbe.ProbeWords)];
            }

            var composed = 0;

            for (var index = 0; (index < cells); index++) {
                var cell = m_restoreCells[index];

                if (Compose(cell: cell, words: m_restoreWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (composed * SdfMeshIrradianceProbe.ProbeWords)))) {
                    m_restoreCells[composed++] = cell;
                }
            }

            lock (m_gate) {
                if (version != m_windowVersion) { continue; }

                for (var index = 0; (index < composed); index++) {
                    var cell = m_restoreCells[index];

                    if (!Holds(cell: cell, dims: m_layout.Dims)) { m_unwritten--; }
                    m_restoreWords.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (index * SdfMeshIrradianceProbe.ProbeWords)).CopyTo(destination: SlotWords(cell: cell));
                    m_dirty = true;
                }
                m_landed.Clear();

                return;
            }
        }
    }
    // One sweep of one probe: its rays against the scene, each hit shaded under the captured lighting, the sweeps after
    // the first adding the light the hit reflects from the cached probes about it; then which traced lights and which
    // neighbouring probes its centre sees.
    private ProbeData TraceProbe((int X, int Y, int Z) cell, int generation, Layout layout, Lighting lighting, (int X, int Y, int Z) low, SdfMeshIrradianceScene scene, int sweep, ProbeBlock block, int index) {
        var centre = (new Vector3(x: (cell.X + 0.5f), y: (cell.Y + 0.5f), z: (cell.Z + 0.5f)) * layout.Spacing);
        var rotation = RotationOf(cell: cell);
        bool valid;
        Vector3 offset;
        float inside;

        if ((sweep > 0) && m_cache.TryGetValue(key: cell, value: out var cached)) {
            // Every sweep stands where the first did, so one origin owns the probe's cubes, planes and neighbours.
            offset = cached.Current.Offset;
            valid = TraceRadiance(inside: out inside, layout: layout, lighting: lighting, low: low, origin: (centre + offset), radiance: block.Radiance(index: index),
                rotation: rotation, scene: scene, sky: block.Sky(index: index), sweep: sweep);
        } else {
            valid = Place(centre: centre, inside: out inside, layout: layout, lighting: lighting, low: low, offset: out offset, radiance: block.Radiance(index: index),
                rotation: rotation, scene: scene, sky: block.Sky(index: index));
        }

        var origin = (centre + offset);
        var lights = block.Lights(index: index);
        var planes = block.Planes(index: index);
        var neighbours = 0u;

        if (valid) {
            // A light past its range lights nothing here and stays unseen; one at the probe's origin is seen.
            foreach (var point in lighting.Points) {
                var toward = (point.Position - origin);
                var distance = toward.Length();

                if ((distance >= point.Range) || ((distance > LightBias) && !scene.Unblocked(direction: (toward / distance), maximum: (distance - LightBias), origin: origin))) { continue; }
                lights[(point.Index >> 5)] |= (1u << (point.Index & 31));
            }
            // The rays aim at the neighbours' centres from where this probe stands, so a moved probe still asks whether
            // it sees each neighbour's cell.
            for (var neighbour = 0; (neighbour < NeighbourOffsets.Length); neighbour++) {
                var neighbourOffset = NeighbourOffsets[neighbour];
                var toward = ((new Vector3(x: neighbourOffset.X, y: neighbourOffset.Y, z: neighbourOffset.Z) * layout.Spacing) - offset);
                var distance = toward.Length();

                var direction = (toward / distance);
                var hit = scene.Intersect(origin: origin, direction: direction, maximum: lighting.Reach, distance: out var depth, material: out _, normal: out var normal);

                if (hit) {
                    if (Vector3.Dot(vector1: normal, vector2: direction) < 0f) { normal = -normal; }
                    planes[neighbour] = new Vector4(value: normal, w: (Vector3.Dot(vector1: normal, vector2: direction) * depth));
                }
                if (!hit || (depth >= distance)) {
                    neighbours |= (1u << neighbour);
                }
            }
        }

        return new ProbeData(Block: block, Generation: generation, Index: index, Inside: inside, Invalid: !valid, Neighbours: neighbours, Offset: offset, Sweeps: (sweep + 1));
    }
    // Where a probe stands in its cell, traced at its first sweep: its centre when that lies in the open at least
    // SdfMeshIrradianceProbe.Clearance from every surface, else a point of its cell that does. The candidates are the
    // centre pushed off the nearest surface along its normal and a lattice a quarter of the spacing apart out to the
    // cell's faces (Candidates); one is kept only when it stands clear and its rays mostly meet front faces. Among the
    // candidates nearest the centre (the pushed point, the inner shell and the axis points on the cell's faces) the one
    // whose rays meet the fewest surfaces within a spacing wins, each quarter of the spacing from the centre costing a
    // sixteenth of its rays: a probe pushed a hand's breadth off a floor or a sign sees furniture and that surface at
    // grazing angles in most directions, and each such first hit's plane cuts a half-space through the cell, where a
    // point a step farther into the open sees past them. Farther candidates are tried in order of
    // distance only when none of those stands. A cell no surface reaches lies wholly inside or wholly outside, so no
    // candidate is tried; a probe no candidate frees stays at its centre, invalid. Returns whether the probe is valid,
    // with its offset and the back-face share of its rays where it stands, and its cubes traced there.
    private bool Place(Vector3 centre, Quaternion rotation, Layout layout, Lighting lighting, (int X, int Y, int Z) low, SdfMeshIrradianceScene scene,
        Span<float> sky, Span<Vector3> radiance, out Vector3 offset, out float inside) {
        var clearance = SdfMeshIrradianceProbe.Clearance;
        var reach = (0.5f * layout.Spacing);

        offset = Vector3.Zero;

        var near = scene.TryNearestSurface(bakeHalfDiagonal: out _, normal: out var normal, point: centre, radius: clearance, surface: out var surface);
        var valid = TraceRadiance(inside: out inside, layout: layout, lighting: lighting, low: low, origin: centre, radiance: radiance, rotation: rotation, scene: scene, sky: sky, sweep: 0);

        if (valid && !near) { return true; }
        if (!near && !scene.TryNearestSurface(bakeHalfDiagonal: out _, normal: out normal, point: centre, radius: ((reach * MathF.Sqrt(x: 3f)) + clearance), surface: out surface)) { return false; }

        var pushed = ((surface + (normal * (1.25f * clearance))) - centre);
        var best = Vector3.Zero;
        var bestScore = float.PositiveInfinity;

        for (var candidate = -1; (candidate < Candidates.Length); candidate++) {
            var trial = ((candidate < 0) ? pushed : (Candidates[candidate] * layout.Spacing));
            var scored = (candidate < ScoredCandidates);

            if (!scored && float.IsFinite(f: bestScore)) { break; }
            if ((MathF.Abs(x: trial.X) > reach) || (MathF.Abs(x: trial.Y) > reach) || (MathF.Abs(x: trial.Z) > reach)) { continue; }

            var origin = (centre + trial);

            if (scene.TryNearestSurface(bakeHalfDiagonal: out _, normal: out _, point: origin, radius: clearance, surface: out _)) { continue; }
            if (Blocked(lighting: lighting, near: out var nearby, origin: origin, reach: layout.Spacing, rotation: rotation, scene: scene)) { continue; }
            if (scored) {
                var score = (nearby + ((0.25f * trial.Length()) / layout.Spacing));

                if (score < bestScore) {
                    best = trial;
                    bestScore = score;
                }
                continue;
            }
            if (!TraceRadiance(inside: out inside, layout: layout, lighting: lighting, low: low, origin: origin, radiance: radiance, rotation: rotation, scene: scene, sky: sky, sweep: 0)) { continue; }

            offset = trial;
            return true;
        }
        if (float.IsFinite(f: bestScore) && TraceRadiance(inside: out inside, layout: layout, lighting: lighting, low: low, origin: (centre + best), radiance: radiance, rotation: rotation, scene: scene, sky: sky, sweep: 0)) {
            offset = best;
            return true;
        }

        // Nothing in the cell stands clear: the centre's own measurement is kept, as an invalid probe's.
        _ = TraceRadiance(inside: out inside, layout: layout, lighting: lighting, low: low, origin: centre, radiance: radiance, rotation: rotation, scene: scene, sky: sky, sweep: 0);
        return false;
    }
    // Whether a point's rays mostly meet back faces, read with a few rays before a candidate is traced in full; `near`
    // receives the share of them that meet any surface within `reach`.
    private static bool Blocked(Vector3 origin, Quaternion rotation, SdfMeshIrradianceScene scene, Lighting lighting, float reach, out float near) {
        const int Rays = 32;
        var backFaces = 0;
        var nearby = 0;

        for (var ray = 0; (ray < Rays); ray++) {
            var direction = Vector3.Transform(value: Fibonacci(index: ray, count: Rays), rotation: rotation);

            if (!scene.Intersect(origin: origin, direction: direction, maximum: lighting.Reach, distance: out var distance, material: out _, normal: out var normal)) { continue; }
            if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) { backFaces++; }
            if (distance <= reach) { nearby++; }
        }
        near = (nearby / ((float)Rays));

        return (backFaces > (InsideShare * Rays));
    }
    // The same hemispherical radiance measurement serves lattice probes and a camera in a gap between them. This
    // reads the existing scene and bounce cache; a camera sample is never written back into the irradiance volume.
    // `inside` receives the share of the rays that met back faces; the measurement is valid while it is at most
    // InsideShare. The cubes are written from zero.
    private bool TraceRadiance(Vector3 origin, Quaternion rotation, Layout layout, Lighting lighting, (int X, int Y, int Z) low,
        SdfMeshIrradianceScene scene, int sweep, Span<float> sky, Span<Vector3> radiance, out float inside) {
        Span<float> total = stackalloc float[6];
        var backFaces = 0;

        sky.Clear();
        radiance.Clear();
        for (var ray = 0; (ray < layout.Rays); ray++) {
            var direction = Vector3.Transform(value: Fibonacci(index: ray, count: layout.Rays), rotation: rotation);
            var hit = scene.Intersect(origin: origin, direction: direction, maximum: lighting.Reach, distance: out var distance, material: out var material, normal: out var normal);
            var light = Vector3.Zero;

            if (hit) {
                if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) {
                    backFaces++;
                } else {
                    light = Shade(layout: layout, lighting: lighting, low: low, material: material, normal: normal, point: (origin + (direction * distance)), scene: scene, sweep: sweep);
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
    private Vector3 Shade(Lighting lighting, Vector3 point, Vector3 normal, int material, int sweep, Layout layout, (int X, int Y, int Z) low, SdfMeshIrradianceScene scene) {
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
            result += ((reflected * Incoming(layout: layout, lighting: lighting, low: low, normal: normal, point: point)) * lighting.Gains.Feedback);
        }

        return result;
    }
    // The light a surface receives from the cached probes about it, as the views pass reads the volume
    // (sdfMeshProbesAt, shade/sdf-mesh-irradiance.hlsli): the eight probes about the point pushed along its normal,
    // clamped to the window's boundary, each weighted trilinearly, by how far it lies in front of the surface and by
    // how far the pushed point lies on the probe's side of its blocking planes. No answering probe means no measured
    // incoming light. It reads each probe's own cubes, which the filtered volume averages over its neighbours.
    private Vector3 Incoming(Lighting lighting, Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z) low) {
        var squared = (normal * normal);
        var skyIrradiance = (lighting.Sky ? (lighting.SkyIrradiance(normal: normal) * lighting.Gains.Sky) : Vector3.Zero);
        Span<float> cornerWeights = stackalloc float[8];
        Span<(int X, int Y, int Z)> cornerCells = stackalloc (int X, int Y, int Z)[8];
        var cornerProbes = new Corners();

        Gather(cells: cornerCells, cornerProbes: ref cornerProbes, cornerWeights: cornerWeights, layout: layout, low: low, normal: normal, point: point, readable: false);

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
    /// corner's cell and its weight after front eligibility, separation and mutual shares, zero for a corner that does
    /// not answer. The lookup clamps to the published window's boundary cells, the visibility tests the receiver pushed
    /// <see cref="SdfMeshIrradianceProbe.NormalBias"/> metres off its surface, and the probes read are the ones views
    /// read: a cell's last complete measurement.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <param name="cells">Receives the eight corners' cells, corner <c>c</c> at offset <c>(c &amp; 1, c &gt;&gt; 1 &amp; 1,
    /// c &gt;&gt; 2)</c> from the lookup's first cell; at least eight long.</param>
    /// <param name="weights">Receives the eight corners' weights; at least eight long.</param>
    /// <returns><see langword="false"/> while the volume holds no window to read.</returns>
    public bool TryGather(Vector3 point, Vector3 normal, Span<(int X, int Y, int Z)> cells, Span<float> weights) =>
        TryGather(cells: cells, normal: normal, point: point, reports: default, weights: weights);
    /// <summary>Reads the eight probes about a receiver as <see cref="TryGather(Vector3, Vector3, Span{ValueTuple{int, int, int}}, Span{float})"/>
    /// does, and reports each corner's record and the terms of its weight.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <param name="cells">Receives the eight corners' cells; at least eight long.</param>
    /// <param name="weights">Receives the eight corners' weights; at least eight long.</param>
    /// <param name="reports">Receives each corner's report; at least eight long.</param>
    /// <returns><see langword="false"/> while the volume holds no window to read.</returns>
    public bool TryGather(Vector3 point, Vector3 normal, Span<(int X, int Y, int Z)> cells, Span<float> weights, Span<SdfMeshProbeReport> reports) {
        Layout layout;
        (int X, int Y, int Z) low;

        lock (m_gate) {
            if ((m_lighting is null) || !m_windowValid) { return false; }
            layout = m_layout;
            low = m_low;
        }

        var probes = new Corners();

        Gather(cells: cells, cornerProbes: ref probes, cornerWeights: weights, layout: layout, low: low, normal: normal, point: point, readable: true, reports: reports);

        return true;
    }
    /// <summary>Describes the eight probes about a receiver as the views pass gathers them: the volume's spacing and
    /// window, then one line a corner with its cell, where its probe stands (its centre plus the offset the trace moved
    /// it by), the share of its rays that met back faces, whether it is valid, its sweeps, how many of its 26 neighbours
    /// it sees, and the terms of its weight at the receiver (trilinear share, facing, the four-plane visibility and its
    /// suspect share, whether it lies in front) with the weight it answers with after separation and mutual shares.</summary>
    /// <param name="point">The receiver's position, in world units.</param>
    /// <param name="normal">The receiver's unit surface normal.</param>
    /// <returns>The lines; one line while the volume holds no window to read.</returns>
    public IReadOnlyList<string> DescribeProbes(Vector3 point, Vector3 normal) {
        var cells = new (int X, int Y, int Z)[8];
        var weights = new float[8];
        var reports = new SdfMeshProbeReport[8];
        Layout layout;
        (int X, int Y, int Z) low;

        lock (m_gate) {
            layout = m_layout;
            low = m_low;
        }
        if (!TryGather(cells: cells, normal: normal, point: point, reports: reports, weights: weights)) {
            return [$"[world.probe: no volume at ({point.X:0.##}, {point.Y:0.##}, {point.Z:0.##}); {Describe()}]"];
        }

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
            $"[world.probe: point=({point.X:0.###}, {point.Y:0.###}, {point.Z:0.###}) normal=({normal.X:0.##}, {normal.Y:0.##}, {normal.Z:0.##}) spacing={layout.Spacing:0.##} window=({low.X}, {low.Y}, {low.Z})+{layout.Dims.X}x{layout.Dims.Y}x{layout.Dims.Z} answering={answering} weight={total:0.####}]"));
        foreach (var report in reports) {
            lines.Add(item: report.Describe());
        }

        return lines;
    }
    // The eight probes about a receiver: each corner's cell, probe and weight after front eligibility, separation and
    // mutual shares, zero where the corner does not answer. The bounce read gathers the probes the trace is landing
    // (Current); a viewer's read gathers the ones views read (Readable). A nonempty `reports` receives each corner's
    // record and the terms of its weight.
    private void Gather(Vector3 point, Vector3 normal, Layout layout, (int X, int Y, int Z) low, bool readable, Span<(int X, int Y, int Z)> cells, Span<float> cornerWeights, ref Corners cornerProbes, Span<SdfMeshProbeReport> reports = default) {
        var pushed = (point + (normal * SdfMeshIrradianceProbe.NormalBias));
        var sample = Vector3.Clamp(
            value1: ((pushed / layout.Spacing) - new Vector3(value: 0.5f)),
            min: new Vector3(x: low.X, y: low.Y, z: low.Z),
            max: new Vector3(x: ((low.X + layout.Dims.X) - 1), y: ((low.Y + layout.Dims.Y) - 1), z: ((low.Z + layout.Dims.Z) - 1))
        );
        var floor = new Vector3(x: MathF.Floor(x: sample.X), y: MathF.Floor(x: sample.Y), z: MathF.Floor(x: sample.Z));
        var fraction = (sample - floor);
        Span<bool> cornerFront = stackalloc bool[8];
        Span<uint> cornerNeighbours = stackalloc uint[8];
        Span<bool> cornerHas = stackalloc bool[8];
        Span<float> cornerSuspect = stackalloc float[8];
        var anyFront = false;

        for (var corner = 0; (corner < 8); corner++) {
            var offset = new Vector3(x: corner & 1, y: (corner >> 1) & 1, z: (corner >> 2) & 1);
            var cell = (((int)(floor.X + offset.X)), ((int)(floor.Y + offset.Y)), ((int)(floor.Z + offset.Z)));

            cells[corner] = cell;
            cornerWeights[corner] = 0f;
            if (!reports.IsEmpty) { reports[corner] = new SdfMeshProbeReport(Cell: cell, Position: ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * layout.Spacing))); }
            if (
                (cell.Item1 < low.X) || (cell.Item1 >= (low.X + layout.Dims.X)) ||
                (cell.Item2 < low.Y) || (cell.Item2 >= (low.Y + layout.Dims.Y)) ||
                (cell.Item3 < low.Z) || (cell.Item3 >= (low.Z + layout.Dims.Z)) ||
                !m_cache.TryGetValue(key: cell, value: out var entry)
            ) {
                continue;
            }

            var probe = (readable ? Readable(probe: entry) : entry.Current);
            var weights = new Vector3(
                x: ((offset.X > 0f) ? fraction.X : (1f - fraction.X)),
                y: ((offset.Y > 0f) ? fraction.Y : (1f - fraction.Y)),
                z: ((offset.Z > 0f) ? fraction.Z : (1f - fraction.Z))
            );
            // The trilinear share is the cell's; the direction, the distance and the plane tests are the probe's own position.
            var probePoint = ((new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * layout.Spacing) + probe.Offset);
            var toward = (probePoint - point);
            var length = toward.Length();
            var facing = ((length > 0f) ? ((Vector3.Dot(vector1: (toward / length), vector2: normal) + 1f) * 0.5f) : 1f);
            var trilinear = ((weights.X * weights.Y) * weights.Z);
            var unseen = 0f;
            var visibility = (probe.Invalid ? 0f : PlaneVisibility(planes: probe.Planes, spacing: layout.Spacing, suspect: out unseen, toward: (pushed - probePoint)));
            var weight = ((trilinear * ((facing * facing) + 0.2f)) * visibility);
            var front = (Vector3.Dot(vector1: toward, vector2: normal) >= (-SdfMeshIrradianceProbe.Behind * layout.Spacing));

            if (!reports.IsEmpty) {
                var sky = 0f;
                var luminance = 0f;

                for (var axis = 0; (axis < 6); axis++) {
                    sky += (probe.Sky[axis] / 6f);
                    luminance += (SdfPhotometry.LuminanceOf(color: probe.Radiance[axis]) / 6f);
                }
                reports[corner] = new SdfMeshProbeReport(Cell: cell, Facing: facing, Front: front, Inside: probe.Inside, Invalid: probe.Invalid, Luminance: luminance, Neighbours: BitOperations.PopCount(value: probe.Neighbours),
                    Offset: probe.Offset, Position: probePoint, Present: true, Sky: sky, Suspect: unseen, Sweeps: probe.Sweeps, Trilinear: trilinear, Visibility: visibility);
            }
            if (probe.Invalid || (weight <= 0f)) { continue; }

            cornerSuspect[corner] = unseen;
            cornerProbes[corner] = probe;
            cornerHas[corner] = true;
            cornerNeighbours[corner] = probe.Neighbours;
            cornerWeights[corner] = weight;
            cornerFront[corner] = front;
            anyFront |= front;
        }

        // The probes in front of the surface answer, and apart those behind it, which answer only where none lies in
        // front; each by the share of the others' weight it sees (MutualShares).
        for (var corner = 0; (corner < 8); corner++) {
            if (!cornerHas[corner] || (cornerFront[corner] != anyFront)) { cornerWeights[corner] = 0f; }
        }
        Separate(first: floor, neighbours: cornerNeighbours, probes: in cornerProbes, pushed: pushed, spacing: layout.Spacing, suspect: cornerSuspect, weights: cornerWeights);
        MutualShares(neighbours: cornerNeighbours, weights: cornerWeights);
        if (!reports.IsEmpty) {
            for (var corner = 0; (corner < 8); corner++) {
                reports[corner] = (reports[corner] with { Weight = cornerWeights[corner] });
            }
        }
    }
    // A probe's 26 rays are one line wide each, so a probe across a roof, floor or wall from the point still admits it
    // through the rays that cleared the blocker's edge or passed a doorway in it (its suspect share, PlaneVisibility).
    // Where another answering probe cannot see it (their neighbour bit is clear), that probe's plane toward it is the
    // blocker between them, and a point on the near side of that plane lies across it from the far probe, which gives
    // way by its suspect share; a probe whose rays met the point's own surface keeps lighting it, so a stair flight
    // between two probes separates neither from the walls beside it. The separating plane has no finite extent: it is
    // the blocker's whole half-space. KEEP IN SYNC with sdfMeshProbeSeparate.
    private static void Separate(in Corners probes, ReadOnlySpan<uint> neighbours, ReadOnlySpan<float> suspect, Span<float> weights, Vector3 first, float spacing, Vector3 pushed) {
        Span<float> support = stackalloc float[64];

        for (var corner = 0; (corner < 8); corner++) {
            for (var other = 0; (other < 8); other++) {
                var pair = ((8 * corner) + other);

                support[pair] = 0f;
                if ((weights[corner] <= 0f) || (suspect[corner] <= 0f) || (other == corner) || (weights[other] <= 0f)) { continue; }

                var x = ((corner & 1) - (other & 1));
                var y = (((corner >> 1) & 1) - ((other >> 1) & 1));
                var z = (((corner >> 2) & 1) - ((other >> 2) & 1));
                var index = NeighbourIndex(x: x, y: y, z: z);

                if (((neighbours[other] >> index) & 1u) != 0u) { continue; }

                var otherPoint = (((first + new Vector3(x: ((other & 1) + 0.5f), y: (((other >> 1) & 1) + 0.5f), z: (((other >> 2) & 1) + 0.5f))) * spacing) + probes[other].Offset);

                var plane = probes[other].Planes[index];
                var past = (Vector3.Dot(vector1: new Vector3(x: plane.X, y: plane.Y, z: plane.Z), vector2: (pushed - otherPoint)) - plane.W);

                support[pair] = (1f - Math.Clamp(max: 1f, min: 0f, value: (past / SdfMeshIrradianceProbe.DepthFade)));
            }
        }
        Separate(support: support, suspect: suspect, weights: weights);
    }

    /// <summary>Scales each of the eight answering probes' weights by the separation the other answering probes
    /// supply. A separator's veto is weighted by its share of the answering probes' weight, at full strength from
    /// <see cref="SdfMeshIrradianceProbe.SeparatorShare"/> of the total, so a probe entering the eight-corner set at a
    /// cell boundary with no trilinear weight vetoes nothing and the gather is continuous across the lattice planes;
    /// and by how much of it survives the same test, so a fully separated probe vetoes nothing and two probes that
    /// separate each other leave each other standing. Both are read from the weights before any veto; a probe that
    /// vetoes another fully is therefore unseparated itself, and the gather never empties. KEEP IN SYNC with
    /// <c>sdfMeshProbeSeparate</c>.</summary>
    /// <param name="support">For each candidate probe <c>c</c> and separator <c>s</c>, at index <c>8 * c + s</c>: how
    /// far the receiver lies on the separator's side of the separator's first-hit plane toward the candidate, from one
    /// (the receiver lies across that blocker from the candidate) to zero (it lies past the blocker, on the candidate's
    /// side); zero wherever the separator sees the candidate or either does not answer.</param>
    /// <param name="suspect">Each probe's suspect share from <see cref="PlaneVisibility"/>.</param>
    /// <param name="weights">The eight weights after front eligibility; zero for each non-answering probe.</param>
    public static void Separate(ReadOnlySpan<float> support, ReadOnlySpan<float> suspect, Span<float> weights) {
        Span<float> share = stackalloc float[8];
        Span<float> shares = stackalloc float[8];
        Span<float> separated = stackalloc float[8];
        var total = 0f;

        for (var corner = 0; (corner < 8); corner++) {
            total += MathF.Max(x: 0f, y: weights[corner]);
        }
        for (var corner = 0; (corner < 8); corner++) {
            share[corner] = ((total > 0f) ? Math.Clamp(max: 1f, min: 0f, value: (weights[corner] / (SdfMeshIrradianceProbe.SeparatorShare * total))) : 0f);
        }
        for (var corner = 0; (corner < 8); corner++) {
            separated[corner] = 0f;
            if (weights[corner] <= 0f) { continue; }

            for (var other = 0; (other < 8); other++) {
                if ((other == corner) || (weights[other] <= 0f)) { continue; }
                separated[corner] = MathF.Max(x: separated[corner], y: ((share[other] * suspect[corner]) * support[((8 * corner) + other)]));
            }
        }
        for (var corner = 0; (corner < 8); corner++) {
            shares[corner] = 1f;
            if (weights[corner] <= 0f) { continue; }

            var veto = 0f;

            for (var other = 0; (other < 8); other++) {
                if ((other == corner) || (weights[other] <= 0f)) { continue; }
                veto = MathF.Max(x: veto, y: (((share[other] * suspect[corner]) * support[((8 * corner) + other)]) * (1f - separated[other])));
            }
            shares[corner] = (1f - veto);
        }
        for (var corner = 0; (corner < 8); corner++) { weights[corner] *= shares[corner]; }
    }

    // Scales each of the eight corner probes' weights (zero for one that does not answer) by how much of the other
    // answering probes' weight it sees and is seen by across the lattice (their neighbour bits), faded in from MutualLow
    // to MutualHigh of it, so a probe that stands across a wall from the rest, which a window or doorway on one of its
    // directions admit it, gives way to them. Where that would leave no weight at all (no answering probe sees
    // another) the weights stand. KEEP IN SYNC with sdfMeshProbeMutualShares.
    private static void MutualShares(ReadOnlySpan<uint> neighbours, Span<float> weights) {
        Span<float> shares = stackalloc float[8];
        var kept = 0f;

        for (var corner = 0; (corner < 8); corner++) {
            shares[corner] = 1f;
            if (weights[corner] <= 0f) { continue; }

            var others = 0f;
            var visible = 0f;

            for (var other = 0; (other < 8); other++) {
                if ((other == corner) || (weights[other] <= 0f)) { continue; }
                others += weights[other];

                var index = NeighbourIndex(x: (((other & 1) - (corner & 1))), y: ((((other >> 1) & 1) - ((corner >> 1) & 1))), z: ((((other >> 2) & 1) - ((corner >> 2) & 1))));

                if ((((neighbours[corner] >> index) & 1u) != 0u) && (((neighbours[other] >> (25 - index)) & 1u) != 0u)) { visible += weights[other]; }
            }

            if (others > 0f) {
                var t = Math.Clamp(max: 1f, min: 0f, value: (((visible / others) - SdfMeshIrradianceProbe.MutualLow) / (SdfMeshIrradianceProbe.MutualHigh - SdfMeshIrradianceProbe.MutualLow)));

                shares[corner] = ((t * t) * (3f - (2f * t)));
            }
            kept += (weights[corner] * shares[corner]);
        }
        if (kept <= 0f) { return; }

        for (var corner = 0; (corner < 8); corner++) {
            weights[corner] *= shares[corner];
        }
    }
    // The neighbour bit of the offset (x, y, z), each in -1, 0 and 1 and not all zero (NeighbourOffsets' order).
    // KEEP IN SYNC with sdfMeshProbeNeighbourIndex.
    private static int NeighbourIndex(int x, int y, int z) {
        var index = (((9 * (x + 1)) + (3 * (y + 1))) + (z + 1));

        return ((index > 13) ? (index - 1) : index);
    }
    private static float Cube(ReadOnlySpan<float> cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));
    private static Vector3 Cube(ReadOnlySpan<Vector3> cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));

    private static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    // Where a probe may stand in its cell apart from its centre, as shares of the spacing: a lattice a quarter of the
    // spacing apart out to the cell's faces, nearest the centre first (Place). The first ScoredCandidates are the inner
    // shell (26 at a quarter of the spacing or less along each axis) and the six axis points on the cell's faces.
    private static readonly Vector3[] Candidates = BuildCandidates();
    private const int ScoredCandidates = 32;

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

    // The 26 offsets in lexicographic order of (x, y, z) over -1, 0 and 1 with the origin left out, so the offset at
    // 25 - n is the negation of the one at n.
    private static (int X, int Y, int Z)[] BuildNeighbourOffsets() {
        var offsets = new List<(int X, int Y, int Z)>(capacity: 26);

        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    if ((x != 0) || (y != 0) || (z != 0)) { offsets.Add(item: (x, y, z)); }
                }
            }
        }

        return [.. offsets];
    }
    private static float[] BuildFilterWeights() {
        var offsets = BuildNeighbourOffsets();
        var weights = new float[offsets.Length];

        for (var index = 0; (index < offsets.Length); index++) {
            var (x, y, z) = offsets[index];

            weights[index] = (1f / ((float)(1 << ((Math.Abs(value: x) + Math.Abs(value: y)) + Math.Abs(value: z)))));
        }

        return weights;
    }
    // The index-th of count directions spread evenly over the sphere (a spherical Fibonacci lattice).
    private static Vector3 Fibonacci(int index, int count) {
        const float GoldenAngle = 2.39996323f;
        var z = (1f - (((2f * index) + 1f) / count));
        var radius = MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (z * z))));
        var angle = (index * GoldenAngle);

        return new Vector3(x: (radius * MathF.Cos(x: angle)), y: z, z: (radius * MathF.Sin(x: angle)));
    }
    // A fixed rotation per cell, so neighbouring probes' rays do not share directions: a uniform random rotation from the
    // cell's PCG3D hash (Pcg3dLatticeNoise.Pcg3d, the kernels' sdfPcg3d), the same on every run and machine.
    private static Quaternion RotationOf((int X, int Y, int Z) cell) {
        var (hash, hash2, _) = Pcg3dLatticeNoise.Pcg3d(x: unchecked((uint)cell.X), y: unchecked((uint)cell.Y), z: unchecked((uint)cell.Z));
        var u1 = ((hash & 0xFFFFu) / 65536f);
        var u2 = ((hash >> 16) / 65536f);
        var u3 = ((hash2 >> 16) / 65536f);
        var a = MathF.Sqrt(x: (1f - u1));
        var b = MathF.Sqrt(x: u1);

        return Quaternion.Normalize(value: new Quaternion(
            x: (a * MathF.Sin(x: ((2f * MathF.PI) * u2))),
            y: (a * MathF.Cos(x: ((2f * MathF.PI) * u2))),
            z: (b * MathF.Sin(x: ((2f * MathF.PI) * u3))),
            w: (b * MathF.Cos(x: ((2f * MathF.PI) * u3)))
        ));
    }

    // One probe: its sky visibility and radiance cubes, the planes of the neighbours' blockers, the traced lights its origin
    // sees (one bit a lights-table index), the neighbouring probes it sees (one bit an entry of NeighbourOffsets), the sweeps
    // it holds, where it stands relative to its cell's centre (Offset, zero unless the trace moved it out of geometry or off
    // a face), the share of its rays that met back faces (Inside) and whether it lies inside geometry. Its cubes and planes
    // are a slice of the floats of the batch it was traced in (ProbeBlock), its lights among them, so a batch allocates one
    // block rather than four arrays a probe, in the large object heap, where a collection of the young generations never
    // copies it.
    private readonly record struct ProbeData(ProbeBlock Block, int Index, uint Neighbours, int Sweeps, int Generation, bool Invalid, Vector3 Offset, float Inside) {
        public Span<float> Sky => Block.Sky(index: Index);
        public Span<Vector3> Radiance => Block.Radiance(index: Index);
        public Span<Vector4> Planes => Block.Planes(index: Index);
        public Span<uint> Lights => Block.Lights(index: Index);
    }
    // The words of one trace batch's probes, each probe's at its index: six sky, six radiance (18 floats) and 26 planes (104
    // floats), then its light words, held as the floats' bits so a batch is one allocation.
    private sealed class ProbeBlock(int count) {
        private const int FloatsPerProbe = (((6 + 18) + 104) + SdfMeshIrradianceProbe.LightWords);

        private readonly float[] m_floats = new float[(count * FloatsPerProbe)];

        public Span<float> Sky(int index) => m_floats.AsSpan(length: 6, start: (index * FloatsPerProbe));
        public Span<Vector3> Radiance(int index) => MemoryMarshal.Cast<float, Vector3>(span: m_floats.AsSpan(length: 18, start: ((index * FloatsPerProbe) + 6)));
        public Span<Vector4> Planes(int index) => MemoryMarshal.Cast<float, Vector4>(span: m_floats.AsSpan(length: 104, start: ((index * FloatsPerProbe) + 24)));
        public Span<uint> Lights(int index) => MemoryMarshal.Cast<float, uint>(span: m_floats.AsSpan(length: SdfMeshIrradianceProbe.LightWords, start: ((index * FloatsPerProbe) + 128)));
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
    // A tier's volume: probe spacing, probes along each axis, rays a probe traces and sweeps (one plus the bounces).
    private readonly record struct Layout(float Spacing, (int X, int Y, int Z) Dims, int Rays, int Sweeps) {
        public int Count => ((Dims.X * Dims.Y) * Dims.Z);

        public static Layout Of(SdfIndirectTier tier, int? bounces) {
            var high = (tier == SdfIndirectTier.High);
            var cap = (high ? 3 : 2);

            return new Layout(
                Dims: (high ? HighDims : MediumDims),
                Rays: (high ? 128 : 96),
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
