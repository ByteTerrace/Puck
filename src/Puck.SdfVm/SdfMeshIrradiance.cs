using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
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
/// geometry sends it (sun and other directional lights under traced shadows, emission, and, from the second sweep, the
/// light the geometry reflects from the probes around each hit, its sky included). A probe whose rays mostly meet back
/// faces lies inside geometry and is stored as invalid. The volume is toroidal: a cell's probe lives in the slot its
/// coordinates name modulo the volume's size, keyed by its cell, so the views pass rejects a slot holding another cell's
/// probe, and a move of the camera rewrites only the slots of cells it brings in.</para>
/// <para>Probes are traced on the thread pool, nearest the camera first and every probe's first sweep before any second,
/// and cached by cell, so a static world traces each cell once per lighting: a camera returning to a place reads its
/// cached probes at once. A change of the static draws rebuilds the scene and retraces every probe; a change of the
/// directional lights, the gains, the sources or the bounces retraces them over the same scene. Until a probe lands, the
/// old one keeps drawing. The dynamic draws (moving bodies) receive the volume's light and do not cast into it.</para>
/// </summary>
public sealed class SdfMeshIrradiance {
    /// <summary>The words one probe takes in the volume table: its key, the sky visibility cube as six halves, and the
    /// radiance cube as eighteen halves (red, green and blue of +X, -X, +Y, -Y, +Z and -Z). KEEP IN SYNC with
    /// <c>shade/sdf-mesh-irradiance.hlsli</c>.</summary>
    public const int ProbeWords = 13;
    /// <summary>The key flag a written probe carries; a slot without it holds no probe.</summary>
    public const uint WrittenFlag = (1u << 30);
    /// <summary>The key flag of a probe inside geometry, which views never read.</summary>
    public const uint InvalidFlag = (1u << 31);
    /// <summary>The most probes a volume holds, the High tier's.</summary>
    public const int MaxProbes = ((48 * 16) * 48);

    private static readonly (int X, int Y, int Z) HighDims = (48, 16, 48);
    private static readonly (int X, int Y, int Z) MediumDims = (32, 12, 32);
    // A probe whose rays meet back faces more than this share of the time lies inside geometry.
    private const float InsideShare = 0.25f;
    // The probes one batch of the trace takes before it looks again at the camera and the lighting.
    private const int BatchProbes = 384;
    // The share of a cell the sample point is pushed along the surface normal before the probes around it are read.
    private const float NormalBias = 0.35f;

    private readonly Lock m_gate = new();
    private readonly ConcurrentDictionary<(int X, int Y, int Z), Probe> m_cache = new();
    private readonly ConcurrentQueue<((int X, int Y, int Z) Cell, Probe Probe)> m_landed = new();
    private readonly uint[] m_volume = new uint[MaxProbes * ProbeWords];
    private readonly Stopwatch m_clock = new();

    private SdfMeshIrradianceScene? m_scene;
    private int m_sceneKey;
    private int m_buildingKey;
    private Task? m_build;
    private Lighting? m_lighting;
    private int m_lightingKey;
    private int m_generation;
    private Layout m_layout;
    private (int X, int Y, int Z) m_low;
    private Vector3 m_camera;
    private bool m_windowValid;
    private Task? m_worker;
    private bool m_dirty;
    // Whether the latest Stage wrote the volume table: the frame it prepared must render even once the trace is done.
    private bool m_wrote;
    private bool m_reported;
    private long m_traced;
    private long m_rays;

    /// <summary>Gets whether probes are being traced or wait to reach the volume table.</summary>
    public bool Settling {
        get {
            lock (m_gate) {
                return ((m_worker is { IsCompleted: false }) || (m_build is { IsCompleted: false }) || !m_landed.IsEmpty || m_dirty || m_wrote);
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
    /// <param name="bakePending">Whether the mesh bake still owes meshes, when the scene waits for it.</param>
    /// <returns>The parameters the views pass reads.</returns>
    public SdfMeshIrradianceParameters Stage(SdfFrame frame, SdfWorldTables tables, SdfIndirectTier tier, bool bakePending) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: tables);

        if ((tier == SdfIndirectTier.Off) || (frame.Views.Count == 0)) { return default; }

        var layout = Layout.Of(tier: tier, bounces: frame.IndirectBounces);

        lock (m_gate) {
            m_wrote = false;
            if (!bakePending) { RefreshScene(frame: frame); }
            RefreshLighting(frame: frame, layout: layout);
            m_camera = frame.Views[0].Camera.Position;

            var low = (
                X: (((int)MathF.Floor(m_camera.X / layout.Spacing)) - (layout.Dims.X / 2)),
                Y: (((int)MathF.Floor(m_camera.Y / layout.Spacing)) - (layout.Dims.Y / 2)),
                Z: (((int)MathF.Floor(m_camera.Z / layout.Spacing)) - (layout.Dims.Z / 2))
            );

            if (!m_windowValid || (low != m_low) || (layout.Dims != m_layout.Dims) || (layout.Spacing != m_layout.Spacing)) {
                var resized = (!m_windowValid || (layout.Dims != m_layout.Dims) || (layout.Spacing != m_layout.Spacing));

                m_low = low;
                m_layout = layout;
                m_windowValid = true;
                if (resized) {
                    Array.Clear(array: m_volume);
                    m_cache.Clear();
                    m_generation++;
                    m_reported = false;
                }
                FillFromCache();
            }
            m_layout = layout;

            while (m_landed.TryDequeue(result: out var landed)) {
                if (InWindow(cell: landed.Cell)) { Store(cell: landed.Cell, probe: landed.Probe); }
            }
            if ((m_scene is not null) && (m_lighting is not null) && (m_worker is null or { IsCompleted: true })) {
                m_worker = Task.Run(action: Trace);
            }
            if (m_dirty) {
                tables.WriteMeshIrradiance(words: m_volume.AsSpan(length: (layout.Count * ProbeWords), start: 0));
                m_dirty = false;
                m_wrote = true;
            }
        }

        return new SdfMeshIrradianceParameters(Dims: layout.Dims, Enabled: true, Low: m_low, Spacing: layout.Spacing);
    }
    /// <summary>Describes the volume: its tier's layout, the static scene, the probes cached and the rays traced.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        lock (m_gate) {
            return $"[sdf.mesh-gi] spacing={m_layout.Spacing:0.##} dims={m_layout.Dims.X}x{m_layout.Dims.Y}x{m_layout.Dims.Z} rays={m_layout.Rays} sweeps={m_layout.Sweeps} triangles={(m_scene?.TriangleCount ?? 0)} cached={m_cache.Count} traced={m_traced} traced-rays={m_rays}";
        }
    }

    // Rebuilds the scene on the thread pool when the static draws changed; the old scene traces until the new one lands.
    private void RefreshScene(SdfFrame frame) {
        var key = 17;
        var draws = 0;

        foreach (var draw in frame.MeshDraws) {
            if (!SdfMeshIrradianceScene.IsStatic(draw: in draw)) { continue; }
            key = HashCode.Combine(value1: key, value2: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: draw.Mesh), value3: draw.ObjectToWorld, value4: draw.Material);
            draws++;
        }
        if ((key == m_sceneKey) || (key == m_buildingKey) || (draws == 0)) { return; }
        if (m_build is { IsCompleted: false }) { return; }

        var snapshot = frame.MeshDraws.ToArray();

        m_buildingKey = key;
        m_build = Task.Run(action: () => {
            var clock = Stopwatch.StartNew();
            var scene = SdfMeshIrradianceScene.Build(draws: snapshot);

            lock (m_gate) {
                m_scene = scene;
                m_sceneKey = key;
                m_buildingKey = 0;
                m_cache.Clear();
                m_generation++;
                m_reported = false;
                m_clock.Restart();
                m_traced = 0;
                m_rays = 0;
            }
            Console.Error.WriteLine(value: $"[sdf.mesh-gi] scene draws={draws} triangles={(scene?.TriangleCount ?? 0)} seconds={clock.Elapsed.TotalSeconds:0.000}");
        });
    }
    // Captures the lighting the probes trace under, and restarts the trace when it changes.
    private void RefreshLighting(SdfFrame frame, Layout layout) {
        var key = HashCode.Combine(value1: frame.IndirectGains, value2: frame.IndirectSources, value3: layout.Sweeps, value4: layout.Rays);

        foreach (var light in frame.Lights.Records) {
            if (light.Kind == SdfLightKind.Directional) { key = HashCode.Combine(value1: key, value2: light); }
        }
        if ((key == m_lightingKey) && (m_lighting is not null)) { return; }

        m_lightingKey = key;
        m_lighting = Lighting.Capture(frame: frame);
        m_cache.Clear();
        m_generation++;
        m_reported = false;
        m_clock.Restart();
        m_traced = 0;
        m_rays = 0;
    }
    private bool InWindow((int X, int Y, int Z) cell) =>
        ((cell.X >= m_low.X) && (cell.X < (m_low.X + m_layout.Dims.X)) && (cell.Y >= m_low.Y) && (cell.Y < (m_low.Y + m_layout.Dims.Y)) &&
        (cell.Z >= m_low.Z) && (cell.Z < (m_low.Z + m_layout.Dims.Z)));
    // Writes every cached probe of the window whose slot holds another cell's.
    private void FillFromCache() {
        var dims = m_layout.Dims;

        for (var z = m_low.Z; (z < (m_low.Z + dims.Z)); z++) {
            for (var y = m_low.Y; (y < (m_low.Y + dims.Y)); y++) {
                for (var x = m_low.X; (x < (m_low.X + dims.X)); x++) {
                    var slot = SlotOf(x: x, y: y, z: z, dims: dims);

                    if ((m_volume[(slot * ProbeWords)] & ~InvalidFlag) == (KeyOf(x: x, y: y, z: z) | WrittenFlag)) { continue; }
                    if (m_cache.TryGetValue(key: (x, y, z), value: out var probe)) { Store(cell: (x, y, z), probe: probe); }
                }
            }
        }
    }
    private void Store((int X, int Y, int Z) cell, Probe probe) {
        var words = m_volume.AsSpan(length: ProbeWords, start: (SlotOf(x: cell.X, y: cell.Y, z: cell.Z, dims: m_layout.Dims) * ProbeWords));

        words[0] = ((KeyOf(x: cell.X, y: cell.Y, z: cell.Z) | WrittenFlag) | (probe.Invalid ? InvalidFlag : 0u));
        for (var pair = 0; (pair < 3); pair++) {
            words[(1 + pair)] = Pack(low: probe.Sky[(2 * pair)], high: probe.Sky[((2 * pair) + 1)]);
        }
        for (var pair = 0; (pair < 9); pair++) {
            words[(4 + pair)] = Pack(low: Channel(cube: probe.Radiance, index: (2 * pair)), high: Channel(cube: probe.Radiance, index: ((2 * pair) + 1)));
        }
        m_dirty = true;
    }
    private static float Channel(Vector3[] cube, int index) {
        var value = cube[(index / 3)];

        return ((index % 3) switch { 0 => value.X, 1 => value.Y, _ => value.Z });
    }
    private static uint Pack(float low, float high) =>
        (((uint)BitConverter.HalfToUInt16Bits(value: (Half)low)) | (((uint)BitConverter.HalfToUInt16Bits(value: (Half)high)) << 16));
    /// <summary>Returns the key a cell's probe carries: each coordinate's low ten bits. KEEP IN SYNC with
    /// <c>sdfMeshIrradianceKey</c>.</summary>
    /// <param name="x">The cell's first coordinate.</param>
    /// <param name="y">The cell's second coordinate.</param>
    /// <param name="z">The cell's third coordinate.</param>
    /// <returns>The key, without its flags.</returns>
    public static uint KeyOf(int x, int y, int z) => ((((uint)x) & 1023u) | ((((uint)y) & 1023u) << 10) | ((((uint)z) & 1023u) << 20));
    /// <summary>Returns the slot a cell's probe takes in a volume: its coordinates modulo the volume's size. KEEP IN SYNC
    /// with <c>sdfMeshIrradianceSlot</c>.</summary>
    /// <param name="x">The cell's first coordinate.</param>
    /// <param name="y">The cell's second coordinate.</param>
    /// <param name="z">The cell's third coordinate.</param>
    /// <param name="dims">The volume's probes along each axis.</param>
    /// <returns>The slot.</returns>
    public static int SlotOf(int x, int y, int z, (int X, int Y, int Z) dims) =>
        (Wrap(value: x, size: dims.X) + (dims.X * (Wrap(value: y, size: dims.Y) + (dims.Y * Wrap(value: z, size: dims.Z)))));
    private static int Wrap(int value, int size) => (((value % size) + size) % size);

    // The trace loop: batches of the window's probes that lack a sweep, fewest sweeps first and nearest the camera
    // first, until every probe of the window holds every sweep. A batch reads the cache as it stood before the batch, so
    // its probes' feedback does not depend on the order the pool runs them in.
    private void Trace() {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(val1: 1, val2: (Environment.ProcessorCount - 2)) };
        var candidates = new List<((int X, int Y, int Z) Cell, int Sweeps, float Distance)>();

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
                        var sweeps = (m_cache.TryGetValue(key: (x, y, z), value: out var cached) ? cached.Sweeps : 0);

                        if (sweeps >= layout.Sweeps) { continue; }

                        var center = (new Vector3(x: (x + 0.5f), y: (y + 0.5f), z: (z + 0.5f)) * layout.Spacing);

                        candidates.Add(item: ((x, y, z), sweeps, Vector3.DistanceSquared(value1: center, value2: camera)));
                    }
                }
            }
            if (candidates.Count == 0) {
                lock (m_gate) {
                    if (!m_reported && (generation == m_generation)) {
                        m_reported = true;
                        Console.Error.WriteLine(value: $"[sdf.mesh-gi] done probes={m_cache.Count} traced={m_traced} rays={m_rays} triangles={scene.TriangleCount} sweeps={layout.Sweeps} seconds={m_clock.Elapsed.TotalSeconds:0.000}");
                    }
                }
                return;
            }

            candidates.Sort(comparison: static (a, b) => ((a.Sweeps != b.Sweeps) ? a.Sweeps.CompareTo(value: b.Sweeps) : a.Distance.CompareTo(value: b.Distance)));

            var count = Math.Min(val1: BatchProbes, val2: candidates.Count);
            var results = new Probe[count];

            _ = Parallel.For(fromInclusive: 0, toExclusive: count, parallelOptions: parallel, body: index => {
                var candidate = candidates[index];

                results[index] = TraceProbe(cell: candidate.Cell, layout: layout, lighting: lighting, scene: scene, sweep: candidate.Sweeps);
            });

            lock (m_gate) {
                if (generation != m_generation) { continue; }
                for (var index = 0; (index < count); index++) {
                    m_cache[candidates[index].Cell] = results[index];
                    m_landed.Enqueue(item: (candidates[index].Cell, results[index]));
                }
                m_traced += count;
                m_rays += ((long)count * layout.Rays);
            }
        }
    }

    // One sweep of one probe: its rays against the scene, each hit shaded under the captured lighting, the sweeps after
    // the first adding the light the hit reflects from the cached probes about it.
    private Probe TraceProbe((int X, int Y, int Z) cell, Layout layout, Lighting lighting, SdfMeshIrradianceScene scene, int sweep) {
        var origin = (new Vector3(x: (cell.X + 0.5f), y: (cell.Y + 0.5f), z: (cell.Z + 0.5f)) * layout.Spacing);
        var rotation = RotationOf(cell: cell);
        Span<float> total = stackalloc float[6];
        Span<float> sky = stackalloc float[6];
        Span<Vector3> radiance = stackalloc Vector3[6];
        var backFaces = 0;

        for (var ray = 0; (ray < layout.Rays); ray++) {
            var direction = Vector3.Transform(value: Fibonacci(index: ray, count: layout.Rays), rotation: rotation);
            var hit = scene.Intersect(origin: origin, direction: direction, maximum: lighting.Reach, distance: out var distance, triangle: out var triangle);
            var light = Vector3.Zero;
            var open = !hit;

            if (hit) {
                var normal = scene.NormalOf(triangle: triangle);

                if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) {
                    backFaces++;
                } else {
                    light = Shade(lighting: lighting, normal: normal, point: (origin + (direction * distance)), scene: scene, material: scene.MaterialOf(triangle: triangle), sweep: sweep, layout: layout);
                }
            }

            for (var axis = 0; (axis < 6); axis++) {
                var weight = MathF.Max(0f, Vector3.Dot(vector1: direction, vector2: Axes[axis]));

                if (weight <= 0f) { continue; }
                total[axis] += weight;
                if (open) { sky[axis] += weight; }
                radiance[axis] += (light * weight);
            }
        }

        var probe = new Probe(Sky: new float[6], Radiance: new Vector3[6], Sweeps: (sweep + 1), Invalid: (backFaces > (InsideShare * layout.Rays)));

        for (var axis = 0; (axis < 6); axis++) {
            if (total[axis] <= 0f) { continue; }
            probe.Sky[axis] = (sky[axis] / total[axis]);
            probe.Radiance[axis] = (radiance[axis] / total[axis]);
        }

        return probe;
    }
    // The radiance a hit sends back along its ray, in the views pass's units (albedo times the light a surface receives):
    // the directional lights' bounce under a traced shadow, its emission, and from the second sweep the reflected light of
    // the probes about it.
    private Vector3 Shade(Lighting lighting, Vector3 point, Vector3 normal, int material, int sweep, Layout layout, SdfMeshIrradianceScene scene) {
        var surface = lighting.MaterialAt(index: material);
        var reflected = ((surface.Albedo * (1f - surface.Metal)) * (surface.Bleed ?? Vector3.One));
        var result = Vector3.Zero;
        var lifted = (point + (normal * 1.0e-3f));

        if (lighting.Direct && (reflected != Vector3.Zero)) {
            var direct = Vector3.Zero;

            foreach (var light in lighting.Lights) {
                var lambert = MathF.Max(0f, ((Vector3.Dot(vector1: normal, vector2: light.Direction) + surface.Wrap) / (1f + surface.Wrap)));

                if (lambert <= 0f) { continue; }
                if (light.CastsShadow && !scene.Unblocked(direction: light.Direction, maximum: lighting.Reach, origin: lifted)) { continue; }
                direct += (light.Color * ((light.Weight * lambert) * light.Bounce));
            }
            result += ((reflected * direct) * lighting.Gains.Lights);
        }
        if (lighting.Emission && (surface.Emissive > 0f)) {
            result += (((surface.Albedo * surface.Emissive) * (surface.Bleed ?? Vector3.One)) * lighting.Gains.Emission);
        }
        if (lighting.Feedback && (sweep > 0) && (reflected != Vector3.Zero)) {
            result += ((reflected * Incoming(lighting: lighting, point: point, normal: normal, layout: layout)) * lighting.Gains.Feedback);
        }

        return result;
    }
    // The light a surface receives from the cached probes about it, weighted as the views pass weights them, or the open
    // sky's when no probe about it is cached.
    private Vector3 Incoming(Lighting lighting, Vector3 point, Vector3 normal, Layout layout) {
        var sample = ((point + (normal * (NormalBias * layout.Spacing))) / layout.Spacing) - new Vector3(value: 0.5f);
        var floor = new Vector3(x: MathF.Floor(sample.X), y: MathF.Floor(sample.Y), z: MathF.Floor(sample.Z));
        var fraction = (sample - floor);
        var squared = (normal * normal);
        var skyIrradiance = (lighting.Sky ? (lighting.SkyIrradiance(normal: normal) * lighting.Gains.Sky) : Vector3.Zero);
        var sum = Vector3.Zero;
        var total = 0f;

        for (var corner = 0; (corner < 8); corner++) {
            var offset = new Vector3(x: (corner & 1), y: ((corner >> 1) & 1), z: ((corner >> 2) & 1));
            var cell = ((int)(floor.X + offset.X), (int)(floor.Y + offset.Y), (int)(floor.Z + offset.Z));

            if (!m_cache.TryGetValue(key: cell, value: out var probe) || probe.Invalid) { continue; }

            var weights = new Vector3(
                x: ((offset.X > 0f) ? fraction.X : (1f - fraction.X)),
                y: ((offset.Y > 0f) ? fraction.Y : (1f - fraction.Y)),
                z: ((offset.Z > 0f) ? fraction.Z : (1f - fraction.Z))
            );
            var probePoint = (new Vector3(x: (cell.Item1 + 0.5f), y: (cell.Item2 + 0.5f), z: (cell.Item3 + 0.5f)) * layout.Spacing);
            var toward = (probePoint - point);
            var length = toward.Length();
            var facing = ((length > 0f) ? ((Vector3.Dot(vector1: (toward / length), vector2: normal) + 1f) * 0.5f) : 1f);
            var weight = (((weights.X * weights.Y) * weights.Z) * ((facing * facing) + 0.2f));

            if (weight <= 0f) { continue; }

            var visibility = Cube(cube: probe.Sky, normal: normal, squared: squared);
            var bounce = Cube(cube: probe.Radiance, normal: normal, squared: squared);

            sum += (((skyIrradiance * visibility) + bounce) * weight);
            total += weight;
        }

        return ((total > 0f) ? (sum / total) : skyIrradiance);
    }
    private static float Cube(float[] cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));
    private static Vector3 Cube(Vector3[] cube, Vector3 normal, Vector3 squared) =>
        (((squared.X * cube[((normal.X >= 0f) ? 0 : 1)]) + (squared.Y * cube[((normal.Y >= 0f) ? 2 : 3)])) + (squared.Z * cube[((normal.Z >= 0f) ? 4 : 5)]));

    private static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    // The index-th of count directions spread evenly over the sphere (a spherical Fibonacci lattice).
    private static Vector3 Fibonacci(int index, int count) {
        const float GoldenAngle = 2.39996323f;
        var z = (1f - (((2f * index) + 1f) / count));
        var radius = MathF.Sqrt(MathF.Max(0f, (1f - (z * z))));
        var angle = (index * GoldenAngle);

        return new Vector3(x: (radius * MathF.Cos(angle)), y: z, z: (radius * MathF.Sin(angle)));
    }
    // A fixed rotation per cell, so neighbouring probes' rays do not share directions.
    private static Quaternion RotationOf((int X, int Y, int Z) cell) {
        var hash = (uint)HashCode.Combine(value1: cell.X, value2: cell.Y, value3: cell.Z);

        hash = ((uint)((hash ^ 0x9E3779B9u) * 0x85EBCA6Bu));

        var u1 = ((hash & 0xFFFFu) / 65536f);
        var u2 = ((hash >> 16) / 65536f);
        var hash2 = (hash * 0xC2B2AE35u);
        var u3 = ((hash2 >> 16) / 65536f);
        var a = MathF.Sqrt(1f - u1);
        var b = MathF.Sqrt(u1);

        return Quaternion.Normalize(value: new Quaternion(
            x: (a * MathF.Sin(2f * MathF.PI * u2)),
            y: (a * MathF.Cos(2f * MathF.PI * u2)),
            z: (b * MathF.Sin(2f * MathF.PI * u3)),
            w: (b * MathF.Cos(2f * MathF.PI * u3))
        ));
    }

    // One probe: its sky visibility and radiance cubes, the sweeps it holds and whether it lies inside geometry.
    private sealed record Probe(float[] Sky, Vector3[] Radiance, int Sweeps, bool Invalid);

    // A tier's volume: probe spacing, probes along each axis, rays a probe traces and sweeps (one plus the bounces).
    private readonly record struct Layout(float Spacing, (int X, int Y, int Z) Dims, int Rays, int Sweeps) {
        public int Count => ((Dims.X * Dims.Y) * Dims.Z);

        public static Layout Of(SdfIndirectTier tier, int? bounces) {
            var high = (tier == SdfIndirectTier.High);
            var cap = (high ? 2 : 1);

            return new Layout(
                Dims: (high ? HighDims : MediumDims),
                Rays: (high ? 128 : 96),
                Spacing: (high ? 1f : 2f),
                Sweeps: (1 + Math.Clamp(value: (bounces ?? cap), min: 0, max: cap))
            );
        }
    }

    // The lighting a generation of probes traces under: the directional lights, the program's materials, the gains and
    // sources, and the sky's irradiance projected on the CPU (SdfSkyEnvironment), which the bounced light reads.
    private sealed class Lighting {
        public required SdfLight[] Lights { get; init; }
        public required SdfMaterial[] Materials { get; init; }
        public required Vector3[] SkyCoefficients { get; init; }
        public required SdfIndirectGains Gains { get; init; }
        public required float Reach { get; init; }
        public bool Direct { get; init; }
        public bool Emission { get; init; }
        public bool Feedback { get; init; }
        public bool Sky { get; init; }

        public SdfMaterial MaterialAt(int index) =>
            (((index >= 0) && (index < Materials.Length)) ? Materials[index] : new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
        // The sky's cosine-weighted mean radiance about a normal: its irradiance over pi, the views pass's unit.
        public Vector3 SkyIrradiance(Vector3 normal) => (SdfSkyEnvironment.Irradiance(coefficients: SkyCoefficients, normal: normal) / MathF.PI);

        public static Lighting Capture(SdfFrame frame) {
            var lights = new List<SdfLight>();

            foreach (var light in frame.Lights.Records) {
                if ((light.Kind == SdfLightKind.Directional) && (light.Weight > 0f)) {
                    lights.Add(item: light with { Direction = SdfLights.UnitDirection(direction: light.Direction) });
                }
            }

            var coefficients = new Vector3[SdfSkyEnvironment.CoefficientCount];
            var sources = frame.IndirectSources;

            if ((sources & SdfIndirectSources.Sky) != 0) {
                var map = new Vector3[SdfSkyEnvironment.Texels];
                var layers = new SdfSkyLayer[SdfSky.MaxLayers];

                frame.Sky.Pack(frame.Lights, frame.FarDistance, new SdfSkyDetails(), out var block, layers);
                SdfSkyEnvironment.Render(block: in block, layers: layers, map: map);
                SdfSkyEnvironment.Project(coefficients: coefficients, map: map);
            }

            return new Lighting {
                Direct = ((sources & SdfIndirectSources.Direct) != 0),
                Emission = ((sources & SdfIndirectSources.Emission) != 0),
                Feedback = ((sources & SdfIndirectSources.Feedback) != 0),
                Gains = frame.IndirectGains,
                Lights = [.. lights],
                Materials = [.. frame.Program.Materials],
                Reach = MathF.Max(1f, frame.FarDistance),
                Sky = ((sources & SdfIndirectSources.Sky) != 0),
                SkyCoefficients = coefficients,
            };
        }
    }
}
