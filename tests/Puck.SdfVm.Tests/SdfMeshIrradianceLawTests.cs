using System.Numerics;
using System.Reflection;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: the irradiance volume keeps a static point light to the room it hangs in. Traced over two rooms stacked on a
/// slab, with one static point light in the upper room and nothing else, a probe in the upper room sees the light (its
/// visibility bit set) and holds the light the slab's top sends back, while a probe in the lower room sees neither; a
/// probe's directional depth stops at the slab, so a read on the slab's far side takes nothing from it;
/// and the meter reads the upper room lit and the lower room dark.
/// </summary>
public sealed partial class SdfMeshIrradianceLawTests : IDisposable {
    // The volumes this law made (Volume), disposed when it ends: a volume subscribes to the shared memory account's trim
    // event, which otherwise holds it and every probe it traced for the rest of the run.
    private readonly List<SdfMeshIrradiance> m_volumes = [];

    /// <summary>Disposes the volumes the law made.</summary>
    public void Dispose() {
        foreach (var volume in m_volumes) { volume.Dispose(); }
        m_volumes.Clear();
    }
    // A volume the law owns, disposed with it.
    private SdfMeshIrradiance Volume() {
        var volume = new SdfMeshIrradiance();

        m_volumes.Add(item: volume);
        return volume;
    }

    [Fact]
    public void ASparselySampledSphereKeepsItsVisibleSurfaceLit() {
        var centre = new Vector3(x: 0f, y: 0f, z: 2f);
        var scene = SdfMeshIrradianceScene.Build([Draw(mesh: Sphere(centre: centre, radius: 0.5f))]);
        var depth = new uint[SdfMeshIrradianceProbe.DepthWords];

        foreach (var origin in new Vector3[] { new(x: 2f, y: -0.2f, z: 1f), new(x: 2f, y: 1.8f, z: 1f), new(x: -0.165f, y: 1.8f, z: 1f) }) {
            _ = SdfMeshProbeDepth.Trace(origin: origin, reach: 5.25f, scene: scene, sheets: false, surfaces: depth);
            for (var latitude = -80; (latitude <= 80); latitude += 5) {
                for (var longitude = 0; (longitude < 360); longitude += 5) {
                    var lat = ((latitude * MathF.PI) / 180f);
                    var lon = ((longitude * MathF.PI) / 180f);
                    var normal = new Vector3(x: (MathF.Cos(x: lat) * MathF.Cos(x: lon)), y: MathF.Sin(x: lat), z: (MathF.Cos(x: lat) * MathF.Sin(x: lon)));
                    var point = (centre + (0.5f * normal));

                    if (Vector3.Dot(vector1: normal, vector2: Vector3.Normalize(value: (origin - point))) <= 0.05f) { continue; }
                    var toward = ((point + (SdfMeshIrradianceProbe.NormalBias * normal)) - origin);

                    if (!scene.Unblocked(origin, Vector3.Normalize(value: toward), (toward.Length() - 0.001f))) { continue; }
                    var visibility = SdfMeshProbeDepth.Visibility(surfaces: depth, toward: toward);

                    Assert.True(condition: (visibility > 0.95f), userMessage: $"sparse sphere origin={origin} receiver={point}: {visibility}");
                }
            }
        }
    }
    [Fact]
    public void AProbeInsideAThinSlabTakesTheNearestExit() {
        var frame = Frame() with {
            ConvergesLighting = true,
            IndirectBounces = 0,
            Lights = new SdfLights(),
            MeshDraws = [Draw(mesh: Box(new Vector3(x: -50f, y: 0.95f, z: -50f), new Vector3(x: 50f, y: 1.05f, z: 50f)))],
        };

        var (parameters, words) = Trace(Volume(), frame, SdfIndirectTier.Medium);
        var probe = Probe(parameters: parameters, words: words, x: 0, y: 0, z: 0);
        var offset = new Vector3(x: Half(probe[SdfMeshIrradianceProbe.OffsetWord], false),
            y: Half(probe[SdfMeshIrradianceProbe.OffsetWord], true), z: Half(probe[(SdfMeshIrradianceProbe.OffsetWord + 1)], false));

        Assert.InRange(offset.Length(), 0.15f, 0.2f);
    }
    [Fact]
    public void AHomogeneousCeilingDoesNotStampTheProbeLatticeIntoItsLight() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Emissive: 1f));

        builder.Sphere(0.1f, material);
        var gains = new SdfIndirectGains(Emission: 1f, Feedback: 0f, Lights: 0f, Screens: 0f, Sky: 0f);
        var frame = Frame() with {
            Program = builder.Build(),
            IndirectGains = gains,
            IndirectSources = gains.Sources,
            ConvergesLighting = true,
            IndirectBounces = 0,
            Lights = new SdfLights(),
            MeshDraws = [Draw(mesh: Box(new Vector3(x: -10000f, y: 4f, z: -10000f), new Vector3(x: 10000f, y: 4.3f, z: 10000f)))],
        };

        var (parameters, words) = Trace(Volume(), frame, SdfIndirectTier.Medium);
        var low = float.PositiveInfinity;
        var high = 0f;

        for (var x = -4; (x <= 4); x++) {
            var value = Luminance(Probe(parameters: parameters, words: words, x: x, y: 0, z: 0), 0);

            low = MathF.Min(x: low, y: value);
            high = MathF.Max(x: high, y: value);
        }
        Assert.True(condition: ((high - low) < 0.001f), userMessage: $"homogeneous horizontal light spans {low}..{high}");
    }
    [Fact]
    public void AVisibleCurvedSurfaceIsNotShadowedByItsNeighboursTangentPlanes() {
        var centre = new Vector3(x: 0f, y: 0f, z: 2f);
        var scene = SdfMeshIrradianceScene.Build([Draw(mesh: Sphere(centre: centre, radius: 0.75f))]);
        var depth = new uint[SdfMeshIrradianceProbe.DepthWords];

        _ = SdfMeshProbeDepth.Trace(scene, Vector3.Zero, 5.25f, depth, false);
        for (var x = -0.55f; (x <= 0.55f); x += 0.05f) {
            for (var y = -0.55f; (y <= 0.55f); y += 0.05f) {
                if (((x * x) + (y * y)) >= 0.42f) { continue; }
                var point = new Vector3(x: x, y: y, z: (2f - MathF.Sqrt(x: (((0.75f * 0.75f) - (x * x)) - (y * y)))));
                var normal = Vector3.Normalize(value: (point - centre));
                var pushed = (point + (SdfMeshIrradianceProbe.NormalBias * normal));

                Assert.True(condition: scene.Unblocked(Vector3.Zero, Vector3.Normalize(value: pushed), (pushed.Length() - 0.001f)));
                var visibility = SdfMeshProbeDepth.Visibility(surfaces: depth, toward: pushed);

                Assert.True(condition: (visibility > 0.95f), userMessage: $"curved receiver ({x}, {y}) visibility={visibility}");
            }
        }
    }
    [Fact]
    public void AFlatSurfaceStaysVisibleAtGrazingAnglesBesideTheDepthReach() {
        var scene = SdfMeshIrradianceScene.Build(draws: [Draw(mesh: Box(low: new Vector3(x: -20f, y: -1.2f, z: -20f), high: new Vector3(x: 20f, y: -1f, z: 20f)))]);
        var depth = new uint[SdfMeshIrradianceProbe.DepthWords];

        _ = SdfMeshProbeDepth.Trace(scene: scene, origin: Vector3.Zero, reach: 5.25f, sheets: false, surfaces: depth);
        for (var radius = 1f; (radius <= 5f); radius += 0.1f) {
            for (var angle = 0; (angle < 360); angle++) {
                var radians = ((angle * MathF.PI) / 180f);
                var toward = new Vector3(x: (radius * MathF.Cos(x: radians)), y: -0.95f, z: (radius * MathF.Sin(x: radians)));
                var visibility = SdfMeshProbeDepth.Visibility(surfaces: depth, toward: toward);

                Assert.True(condition: (visibility > 0.999f), userMessage: $"flat receiver radius={radius} angle={angle} visibility={visibility}");
            }
        }
    }
    [Fact]
    public void AReceiverBehindAnOccluderBesideAnOpenEdgeStaysDark() {
        var scene = SdfMeshIrradianceScene.Build([Draw(mesh: Box(new Vector3(x: 1f, y: -10f, z: -10f), new Vector3(x: 1.1f, y: 0.11f, z: 10f)))]);
        var depth = new uint[SdfMeshIrradianceProbe.DepthWords];

        Assert.InRange(SdfMeshProbeDepth.Trace(scene, Vector3.Zero, 2.65f, depth, false), 1, 256);
        var toward = new Vector3(x: 2f, y: 0.19f, z: 0f);

        Assert.False(condition: scene.Unblocked(Vector3.Zero, Vector3.Normalize(value: toward), toward.Length()));
        var visibility = SdfMeshProbeDepth.Visibility(surfaces: depth, toward: toward);

        Assert.True(condition: (visibility < 0.05f), userMessage: $"silhouette visibility={visibility}");
    }
    /// <summary>THE LAW: a probe's surface map tells a sheet from a solid's near side. A probe stands over a 0.3 m slab
    /// that ends a metre to its west, beside a hill 50 m in radius whose near side bulges 10 m to its east: every texel
    /// whose ray meets the slab (its top or its edge) is marked a sheet, and no texel whose ray meets the hill is.</summary>
    [Fact]
    public void ASurfaceMapMarksASlabASheetAndAHillsNearSideASolid() {
        using var scene = SdfMeshIrradianceScene.Build([
            Draw(mesh: Box(low: new Vector3(x: -20f, y: -1.3f, z: -20f), high: new Vector3(x: -1f, y: -1f, z: 20f))),
            Draw(mesh: Sphere(centre: new Vector3(x: 60f, y: 0f, z: 0f), radius: 50f)),
        ]);
        var surfaces = new uint[SdfMeshIrradianceProbe.DepthWords];
        var directions = SdfMeshProbeDepth.RayDirections;
        var slab = 0;
        var hill = 0;

        _ = SdfMeshProbeDepth.Trace(origin: Vector3.Zero, reach: 30f, scene: scene, sheets: true, surfaces: surfaces);
        for (var texel = 0; (texel < directions.Length); texel++) {
            if (!scene.Intersect(direction: directions[texel], distance: out var distance, material: out _, maximum: 30f, normal: out _, origin: Vector3.Zero)) { continue; }

            var sheet = (((surfaces[(directions.Length + (texel >> 1))] >> (16 * (texel & 1))) & 1u) != 0u);
            var hit = (directions[texel] * distance);

            if (hit.X < 0f) {
                slab++;
                Assert.True(condition: sheet, userMessage: $"texel {texel} meets the slab at {hit} unmarked");
            } else {
                hill++;
                Assert.False(condition: sheet, userMessage: $"texel {texel} meets the hill at {hit} marked a sheet");
            }
        }
        Assert.True(condition: ((slab > 0) && (hill > 0)), userMessage: $"slab texels={slab} hill texels={hill}");
    }
    [Fact]
    public void ASealedEnclosureWithALightBeyondItsWallStaysDark() {
        var frame = Frame() with { ConvergesLighting = true, IndirectBounces = 0 };
        // No lattice centre fits in this sealed 18-cm enclosure; the light is above its roof.
        frame = frame with {
            MeshDraws = [.. frame.MeshDraws,
            Draw(mesh: Box(new Vector3(x: -10f, y: 0f, z: -10f), new Vector3(x: -0.09f, y: 4f, z: 10f))),
            Draw(mesh: Box(new Vector3(x: 0.09f, y: 0f, z: -10f), new Vector3(x: 10f, y: 4f, z: 10f))),
            Draw(mesh: Box(new Vector3(x: -0.09f, y: 0f, z: -10f), new Vector3(x: 0.09f, y: 4f, z: -0.09f))),
            Draw(mesh: Box(new Vector3(x: -0.09f, y: 0f, z: 0.09f), new Vector3(x: 0.09f, y: 4f, z: 10f))),
        ],
        };
        var volume = Volume();

        _ = Trace(volume, frame);
        Assert.Equal(0f, volume.LightVisibility(new Vector3(x: 0f, y: 2f, z: 0f), Vector3.UnitY, 0));
    }
    /// <summary>A light change relights the probes' cached hits and traces nothing; a budget bounds the relights each
    /// frame grants.</summary>
    [Fact]
    public void ALightChangeRelightsWithoutATraceUnderTheFramesBudget() {
        var volume = Volume();
        var frame = Frame();

        _ = Trace(frame: frame, tier: SdfIndirectTier.Medium, volume: volume);
        var traced = volume.Counters.Read(kind: SdfMeshIrradiance.Traced);
        var relit = volume.Counters.Read(kind: SdfMeshIrradiance.Relit);
        var lights = new SdfLights();

        lights.Set(index: 0, light: frame.Lights.Records[0] with { Weight = (0.5f * frame.Lights.Records[0].Weight) });
        lights.Count = 1;
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);

        var dimmed = frame with { Lights = lights, IndirectBudget = new SdfIndirectBudget(Relight: 100, Trace: 0, Age: 0f) };

        _ = volume.Stage(bakePending: false, frame: dimmed, tier: SdfIndirectTier.Medium, write: _ => { });
        _ = SpinWait.SpinUntil(condition: () => (volume.Counters.Read(kind: SdfMeshIrradiance.Relit) >= (relit + 100)), millisecondsTimeout: 30_000);
        // One frame's grant bounds the relights before the next frame grants more.
        Assert.Equal(expected: 100L, actual: (volume.Counters.Read(kind: SdfMeshIrradiance.Relit) - relit));
        _ = Trace(frame: dimmed with { IndirectBudget = SdfIndirectBudget.Unbounded }, tier: SdfIndirectTier.Medium, volume: volume);
        Assert.Equal(expected: traced, actual: volume.Counters.Read(kind: SdfMeshIrradiance.Traced));
        Assert.True(condition: (volume.Counters.Read(kind: SdfMeshIrradiance.Relit) > (relit + 200L)));
    }
    [Fact]
    public void AProbeTracesOneHitBufferOfOneRayPerTexel() {
        var volume = Volume();

        var (parameters, _) = Trace(volume, Frame() with { ConvergesLighting = true, IndirectBounces = 0, MeshDraws = [] }, SdfIndirectTier.Medium);
        var description = volume.Describe();
        var count = long.Parse(description.Split("traced-rays=")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

        var probes = 0L;

        for (var level = 0; (level < SdfMeshIrradianceProbe.Levels); level++) { probes += parameters.Window(level: level).Count; }
        Assert.Equal(expected: (probes * SdfMeshProbeDepth.HitCount), actual: count);
    }
    [Fact]
    public void AProbesPublishedSkyIsItsOwnMeasuredVisibility() {
        var volume = Volume();

        var (parameters, words) = Trace(volume, Frame() with { ConvergesLighting = true, IndirectBounces = 0 });
        var reports = new SdfMeshProbeReport[SdfMeshIrradiance.GatherSlots];
        var cells = new (int X, int Y, int Z)[SdfMeshIrradiance.GatherSlots];
        var weights = new float[SdfMeshIrradiance.GatherSlots];

        Assert.True(condition: volume.TryGather(new Vector3(x: -9.5f, y: 1.5f, z: 0.5f), Vector3.UnitY, cells, weights, reports, out _));
        var own = Assert.Single(collection: reports[..8], predicate: r => (r.Cell == (-10, 1, 0)));
        var probe = Probe(parameters: parameters, words: words, x: -10, y: 1, z: 0);
        var published = 0f;

        for (var axis = 0; (axis < 3); axis++) {
            published += ((Half(probe[(1 + axis)], false) + Half(probe[(1 + axis)], true)) / 6f);
        }
        Assert.True(condition: (MathF.Abs(x: (published - own.Sky)) < 0.001f), userMessage: $"own sky={own.Sky}; published sky={published}");
    }
    [Fact]
    public void AFiniteBlockerShadowsItsOwnDirectionsAndAWallStopsTheFarProbe() {
        var floor = Draw(mesh: Box(low: new Vector3(x: -10f, y: -0.3f, z: -10f), high: new Vector3(x: 10f, y: 0f, z: 10f)));
        // Between the old 26 directions: the receiver ray hits the finite block while +X and +X+Y miss it.
        var frame = Frame() with {
            ConvergesLighting = true,
            IndirectBounces = 0,
            MeshDraws = [floor, Draw(mesh: Box(low: new Vector3(x: 0.85f, y: 0.56f, z: 0.3f), high: new Vector3(x: 1f, y: 0.8f, z: 0.7f)))],
        };
        var volume = Volume();

        _ = Trace(frame: frame, volume: volume);
        var cells = new (int X, int Y, int Z)[SdfMeshIrradiance.GatherSlots];
        var weights = new float[SdfMeshIrradiance.GatherSlots];
        var reports = new SdfMeshProbeReport[SdfMeshIrradiance.GatherSlots];

        Assert.True(condition: volume.TryGather(new Vector3(x: 1.4f, y: 0.8f, z: 0.5f), -Vector3.UnitX, cells, weights, reports, out _));
        var blocked = Assert.Single(collection: reports[..8], predicate: r => (r.Cell == (0, 0, 0)));

        Assert.True(condition: (blocked.Visibility < 0.05f), userMessage: $"finite shadow visibility={blocked.Visibility}");
        Assert.True(condition: volume.TryGather(new Vector3(x: 1.4f, y: 1.3f, z: 0.5f), -Vector3.UnitX, cells, weights, reports, out _));
        var clear = Assert.Single(collection: reports[..8], predicate: r => (r.Cell == (0, 0, 0)));

        Assert.True(condition: (clear.Visibility > 0.95f), userMessage: $"beyond the block's extent visibility={clear.Visibility}");

        var wall = frame with { MeshDraws = [floor, Draw(mesh: Box(low: new Vector3(x: 0.85f, y: 0f, z: -10f), high: new Vector3(x: 1f, y: 5f, z: 10f)))] };

        volume = Volume();
        _ = Trace(frame: wall, volume: volume);
        Assert.True(condition: volume.TryGather(new Vector3(x: 1.4f, y: 1.3f, z: 0.5f), -Vector3.UnitX, cells, weights, reports, out _));
        var hidden = Assert.Single(collection: reports[..8], predicate: r => (r.Cell == (0, 0, 0)));

        Assert.True(condition: (hidden.Visibility < 0.001f), userMessage: $"through-wall visibility={hidden.Visibility} {string.Join(separator: " | ", values: volume.DescribeProbes(new Vector3(x: 1.4f, y: 1.3f, z: 0.5f), -Vector3.UnitX))}");
    }
    [Fact]
    public void ASphereBelowAHangingSignReadsContinuouslyAcrossItsProbeColumn() {
        var frame = Frame() with {
            ConvergesLighting = true,
            IndirectBounces = 0,
            MeshDraws = [
                Draw(mesh: Box(new Vector3(x: -10f, y: -0.3f, z: -10f), new Vector3(x: 10f, y: 0f, z: 10f))),
                Draw(mesh: Sphere(centre: new Vector3(x: 1f, y: 1.1f, z: 2f), radius: 0.75f)),
                Draw(mesh: Box(new Vector3(x: 0.8f, y: 2.8f, z: 0.5f), new Vector3(x: 1.2f, y: 3.2f, z: 3.5f))),
            ],
        };
        var volume = Volume();

        _ = Trace(frame: frame, tier: SdfIndirectTier.Medium, volume: volume);
        var cells = new (int X, int Y, int Z)[SdfMeshIrradiance.GatherSlots];
        var weights = new float[SdfMeshIrradiance.GatherSlots];
        // Both halves, across x = 1 (the sign contains this column's upper probes).
        foreach (var height in new[] { -0.4f, 0f, 0.4f, 0.65f }) {
            Dictionary<(int, int, int), float>? previous = null;

            foreach (var x in new[] { -0.002f, 0f, 0.002f }) {
                var normal = Vector3.Normalize(value: new Vector3(x: x, y: height, z: -MathF.Sqrt(x: (((0.75f * 0.75f) - (height * height)) - (x * x)))));
                var point = (new Vector3(x: 1f, y: 1.1f, z: 2f) + (0.75f * normal));

                Assert.True(condition: volume.TryGather(cells: cells, normal: normal, point: point, weights: weights));
                var total = weights.Sum();

                Assert.True(condition: (total > 0.001f), userMessage: $"sphere height={height} total={total}");
                var current = Enumerable.Range(count: SdfMeshIrradiance.GatherSlots, start: 0).GroupBy(keySelector: i => cells[i]).ToDictionary(g => g.Key, g => (g.Sum(selector: i => weights[i]) / total));

                if (previous is not null) {
                    foreach (var cell in current.Keys.Union(second: previous.Keys)) {
                        Assert.True(condition: (MathF.Abs(x: (current.GetValueOrDefault(key: cell) - previous.GetValueOrDefault(key: cell))) < 0.03f),
                            userMessage: $"sphere height={height} x={x} cell={cell}: {previous.GetValueOrDefault(key: cell)} -> {current.GetValueOrDefault(key: cell)}");
                    }
                }
                previous = current;
            }
        }
    }

    private static SdfMesh Sphere(Vector3 centre, float radius) {
        const int Rows = 32, Columns = 64;
        var positions = new Vector3[((Rows + 1) * (Columns + 1))];
        var indices = new List<uint>();

        for (var y = 0; (y <= Rows); y++) {
            var latitude = ((MathF.PI * y) / Rows);

            for (var x = 0; (x <= Columns); x++) {
                var longitude = (((2f * MathF.PI) * x) / Columns);

                positions[((y * (Columns + 1)) + x)] = (centre + (radius * new Vector3(x: (MathF.Sin(x: latitude) * MathF.Cos(x: longitude)), y: MathF.Cos(x: latitude), z: (MathF.Sin(x: latitude) * MathF.Sin(x: longitude)))));
                if ((y == Rows) || (x == Columns)) { continue; }
                var a = ((uint)((y * (Columns + 1)) + x));
                var b = ((a + Columns) + 1);

                indices.AddRange(collection: [a, (a + 1), b, (a + 1), (b + 1), b]);
            }
        }
        return new SdfMesh(positions: positions, indices: indices.ToArray());
    }

    [Fact]
    public void AProbeReadMarksThePendingPublicationUnsettled() {
        var volume = Volume();
        var frame = Frame() with { ConvergesLighting = true, IndirectBounces = 0 };

        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.Medium, write: _ => { });
        Assert.Contains("unsettled; published-window=", volume.DescribeProbes(Vector3.One, Vector3.UnitY)[0]);
        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.Medium, write: _ => { });
        Assert.DoesNotContain("unsettled", volume.DescribeProbes(Vector3.One, Vector3.UnitY)[0]);
    }
    [Fact]
    public void RemovingTheLastStaticDrawReplacesItsSceneAndProbeMeasurements() {
        var volume = Volume();
        var frame = Frame() with { ConvergesLighting = true, IndirectBounces = 0 };
        uint[] before = [];
        uint[] after = [];

        _ = volume.Stage(frame: frame, tier: SdfIndirectTier.Medium, bakePending: false, write: words => before = words.ToArray());
        Assert.True(condition: (volume.DiagnosticScene(frame: frame)!.TriangleCount > 0));
        // Dynamic and glass surfaces receive light but contribute no opaque static occluder.
        var empty = frame with { MeshDraws = [frame.MeshDraws[0] with { IsDynamic = true }, frame.MeshDraws[1] with { Glass = true }] };

        _ = volume.Stage(frame: empty, tier: SdfIndirectTier.Medium, bakePending: false, write: words => after = words.ToArray());
        var scene = Assert.IsType<SdfMeshIrradianceScene>(@object: volume.DiagnosticScene(frame: empty));

        Assert.Equal(expected: 0, actual: scene.TriangleCount);
        Assert.True(condition: scene.Unblocked(origin: new Vector3(x: 0, y: 2, z: 0), direction: Vector3.UnitY, maximum: 20));
        Assert.False(condition: scene.Intersect(new Vector3(x: 0, y: 2, z: 0), Vector3.UnitY, 20, out _, out _, out _));
        Assert.False(condition: scene.TryNearestSurface(new Vector3(x: 0, y: 4, z: 0), 20, out _, out _, out _));
        Assert.NotEmpty(collection: after);
        Assert.False(condition: before.AsSpan().SequenceEqual(other: after));
        _ = volume.Stage(frame: empty, tier: SdfIndirectTier.Medium, bakePending: false, write: _ => throw new InvalidOperationException(message: "A steady empty scene rewrote its probes."));
        Assert.False(condition: volume.Settling);
    }
    /// <summary>THE LAW: the gather is continuous across a lattice plane. A probe enters the eight-corner set at a cell
    /// boundary with no trilinear weight, so the gather it joins reads as the gather it left. A finite plinth gives
    /// nearby probes different directional depths; an untraced receiver beside it still reads continuous weights a
    /// hair either side of the column's lattice plane.</summary>
    [Fact]
    public void AReceiverCrossingACellBoundaryReadsAContinuousGather() {
        // A floor, and a plinth 0.75 m high between x 0.1 and 0.3, clear of the probe at (0.5, 0.5, 0.5) by twice the
        // clearance so that probe stands at its centre: the probe at (-0.5, 1.5, 0.5) looking down toward it meets
        // the plinth's top, so neither sees the other, and the receiver at y 1 lies on the upper probe's side of that top.
        var frame = Frame() with {
            IndirectBounces = 0,
            MeshDraws = [
                Draw(mesh: Box(low: new Vector3(x: -10f, y: -0.3f, z: -10f), high: new Vector3(x: 10f, y: 0f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: 0.1f, y: 0f, z: -5f), high: new Vector3(x: 0.3f, y: 0.75f, z: 5f))),
            ],
        };
        var volume = Volume();

        _ = Trace(frame: frame, volume: volume);

        // The receiver faces west, so the lookup reads it pushed 5 cm west: these straddle the plane at x 0.5.
        var west = Gather(volume: volume, point: new Vector3(x: 0.549f, y: 1f, z: 0.5f));
        var east = Gather(volume: volume, point: new Vector3(x: 0.551f, y: 1f, z: 0.5f));

        // The probe below the column answers on both sides, while the upper west probe leaves the set with zero
        // trilinear share: west of the plane the gather reads as it does east of it. The weights are the answer's shares,
        // summing to one, so a cell's share moves by at most about a percent across the plane.
        Assert.True(condition: (west[(0, 0, 0)] > 0f));
        Assert.True(condition: (west[(-1, 1, 0)] > 0f));
        Assert.False(condition: east.ContainsKey(key: (-1, 1, 0)));
        foreach (var cell in west.Keys.Union(second: east.Keys)) {
            Assert.InRange(actual: MathF.Abs(x: (west.GetValueOrDefault(key: cell) - east.GetValueOrDefault(key: cell))), high: 0.0125f, low: 0f);
        }
    }
    /// <summary>THE LAW: a probe stands in the open. A lattice centre on a wall's face (where the face under the origin is
    /// never a ray's first hit, so the rays into the wall meet its far face from inside) moves off the face into the room
    /// by at least <see cref="SdfMeshIrradianceProbe.Clearance"/>, carries that offset in its record, and answers for the
    /// wall's receivers, where an invalid probe would leave them the next column alone; a centre inside a pillar moves
    /// out of it; a centre buried farther than its cell reaches stays at its centre, invalid, with no offset. A probe
    /// beyond the wall's end whose ray runs along the face's plane traces through along it, since the coplanar ray meets
    /// none of that face's triangles, and its depth read stays open.</summary>
    [Fact]
    public void AProbeOnAWallFaceOrInsideGeometryMovesIntoTheOpenAndABuriedOneStaysInvalid() {
        // A wall whose +X face lies on x = 0.5, the plane of cell 0's centres, from z = 0 to z = 6: the probe at
        // (0.5, 0.5, 0.5) stands on the face, and the probe at (0.5, 0.5, -0.5) beyond its south end.
        var frame = Frame() with {
            IndirectBounces = 0,
            MeshDraws = [
                Draw(mesh: Box(low: new Vector3(x: -10f, y: -0.3f, z: -10f), high: new Vector3(x: 10f, y: 0f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: 0.2f, y: 0f, z: 0f), high: new Vector3(x: 0.5f, y: 3f, z: 6f))),
            ],
        };
        var volume = Volume();

        var (parameters, words) = Trace(volume: volume, frame: frame);
        var onFace = Probe(parameters: in parameters, words: words, x: 0, y: 0, z: 0);
        var beyondEnd = Probe(parameters: in parameters, words: words, x: 0, y: 0, z: -1);
        var offset = Offset(probe: onFace);

        Assert.Equal(expected: 0u, actual: onFace[0] & SdfMeshIrradianceProbe.InvalidFlag);
        // Into the room, never into the wall, and within the cell; the open preference may also lift it off the floor.
        Assert.InRange(actual: offset.X, high: 0.5f, low: SdfMeshIrradianceProbe.Clearance);
        Assert.InRange(actual: offset.Y, high: 0.5f, low: 0f);
        Assert.InRange(actual: MathF.Abs(x: offset.Z), high: 0.5f, low: 0f);
        Assert.InRange(actual: Inside(probe: onFace), high: 0.25f, low: 0f);
        Assert.True(condition: (SdfMeshProbeDepth.Visibility(surfaces: beyondEnd[SdfMeshIrradianceProbe.DepthWord..], toward: (Vector3.UnitZ * 0.5f)) > 0.95f));

        // The wall's own face reads the moved probe, and the report names where it stands.
        var reports = new SdfMeshProbeReport[SdfMeshIrradiance.GatherSlots];

        Assert.True(condition: volume.TryGather(cells: new (int X, int Y, int Z)[SdfMeshIrradiance.GatherSlots], normal: Vector3.UnitX, point: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f), reports: reports, selection: out _, weights: new float[SdfMeshIrradiance.GatherSlots]));
        var moved = Assert.Single(collection: reports[..8], predicate: static report => (report.Cell == (0, 0, 0)));

        Assert.True(condition: moved.Present);
        Assert.False(condition: moved.Invalid);
        Assert.Equal(expected: (new Vector3(x: 0.5f, y: 0.5f, z: 0.5f) + offset), actual: moved.Position);
        Assert.True(condition: (moved.Weight > 0f));
        Assert.Contains(expectedSubstring: "moved", actualString: moved.Describe());

        // A pillar 0.4 m square about the centre of cell (0, 0, 0): the probe moves out of it, clear of its faces.
        var pillar = frame with {
            MeshDraws = [frame.MeshDraws[0], Draw(mesh: Box(low: new Vector3(x: 0.3f, y: 0f, z: 0.3f), high: new Vector3(x: 0.7f, y: 3f, z: 0.7f)))],
        };

        (parameters, words) = Trace(volume: Volume(), frame: pillar);
        var inPillar = Probe(parameters: in parameters, words: words, x: 0, y: 0, z: 0);
        var outward = Offset(probe: inPillar);

        Assert.Equal(expected: 0u, actual: inPillar[0] & SdfMeshIrradianceProbe.InvalidFlag);
        Assert.True(condition: ((MathF.Abs(x: outward.X) >= (0.2f + SdfMeshIrradianceProbe.Clearance)) || (MathF.Abs(x: outward.Z) >= (0.2f + SdfMeshIrradianceProbe.Clearance))));
        Assert.InRange(actual: MathF.Max(x: MathF.Abs(x: outward.X), y: MathF.Max(x: MathF.Abs(x: outward.Y), y: MathF.Abs(x: outward.Z))), high: 0.5f, low: 0f);

        // A block 3 m thick over the whole floor: no point of cell (0, 0, 0) stands clear, so its probe stays put, invalid.
        var buried = frame with {
            MeshDraws = [frame.MeshDraws[0], Draw(mesh: Box(low: new Vector3(x: -10f, y: 0f, z: -10f), high: new Vector3(x: 10f, y: 3f, z: 10f)))],
        };

        (parameters, words) = Trace(volume: Volume(), frame: buried);
        var slot = SdfMeshIrradiance.SlotOf(dims: parameters.Finest.Dims, x: 0, y: 0, z: 0);
        var deep = words.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (slot * SdfMeshIrradianceProbe.ProbeWords));

        Assert.Equal(expected: SdfMeshIrradianceProbe.InvalidFlag, actual: deep[0] & SdfMeshIrradianceProbe.InvalidFlag);
        Assert.Equal(expected: Vector3.Zero, actual: Offset(probe: deep));
        Assert.True(condition: (Inside(probe: deep) > 0.25f));
    }

    // The probe's offset from its cell's centre, as its record carries it.
    private static Vector3 Offset(ReadOnlySpan<uint> probe) =>
        new(x: Half(high: false, word: probe[SdfMeshIrradianceProbe.OffsetWord]), y: Half(high: true, word: probe[SdfMeshIrradianceProbe.OffsetWord]), z: Half(high: false, word: probe[(SdfMeshIrradianceProbe.OffsetWord + 1)]));
    // The share of the probe's rays that met back faces, as its record carries it.
    private static float Inside(ReadOnlySpan<uint> probe) => Half(high: true, word: probe[(SdfMeshIrradianceProbe.OffsetWord + 1)]);
    // The answering probes about an untraced receiver facing west, by cell.
    private static Dictionary<(int X, int Y, int Z), float> Gather(SdfMeshIrradiance volume, Vector3 point) {
        var cells = new (int X, int Y, int Z)[SdfMeshIrradiance.GatherSlots];
        var weights = new float[SdfMeshIrradiance.GatherSlots];

        Assert.True(condition: volume.TryGather(cells: cells, normal: -Vector3.UnitX, point: point, weights: weights));

        var result = new Dictionary<(int X, int Y, int Z), float>();

        for (var corner = 0; (corner < SdfMeshIrradiance.GatherSlots); corner++) {
            if (weights[corner] > 0f) { result[cells[corner]] = (result.GetValueOrDefault(key: cells[corner]) + weights[corner]); }
        }

        return result;
    }
    // A box between two corners, its faces wound outward, as the bake winds them.
    private static SdfMesh Box(Vector3 low, Vector3 high) {
        Vector3[] positions = [
            new(x: low.X, y: low.Y, z: low.Z), new(x: high.X, y: low.Y, z: low.Z), new(x: high.X, y: high.Y, z: low.Z), new(x: low.X, y: high.Y, z: low.Z),
            new(x: low.X, y: low.Y, z: high.Z), new(x: high.X, y: low.Y, z: high.Z), new(x: high.X, y: high.Y, z: high.Z), new(x: low.X, y: high.Y, z: high.Z),
        ];
        uint[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];

        return new SdfMesh(indices: indices, positions: positions);
    }
    private static SdfMeshDraw Draw(SdfMesh mesh) => new(Identity: new object(), Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity);
    // Two rooms 20 m square: a floor under the lower one, a slab between them from y = 4 to 4.3, and a ceiling over the
    // upper one; a static 20,000-candela point light in the upper room, 1 m over the slab, and no other light.
    private static SdfFrame Frame() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.8f)));

        builder.Sphere(material: material, radius: 0.1f);

        var lights = new SdfLights();

        lights.Set(index: 0, light: new SdfLight(Color: Vector3.One, Direction: new Vector3(x: 0f, y: 5.3f, z: 0f), Kind: SdfLightKind.Point, Param: 40f, Shadows: true, Weight: SdfPhotometry.PositionalWeight(candela: 20000f)));
        lights.Count = 1;
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);

        var gains = new SdfIndirectGains(Emission: 0f, Feedback: 1f, Lights: 1f, Screens: 0f, Sky: 0f);

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: true,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 0f, y: 2f, z: 0f), target: new Vector3(x: 1f, y: 2f, z: 0f), viewportHeight: 4u, viewportWidth: 4u), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))],
            Time: 0f
        ) {
            FarDistance = 60f,
            IndirectGains = gains,
            IndirectSources = gains.Sources,
            IndirectTier = SdfIndirectTier.High,
            Lights = lights,
            MeshDraws = [
                Draw(mesh: Box(low: new Vector3(x: -10f, y: -0.3f, z: -10f), high: new Vector3(x: 10f, y: 0f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: -10f, y: 4f, z: -10f), high: new Vector3(x: 10f, y: 4.3f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: -10f, y: 8.3f, z: -10f), high: new Vector3(x: 10f, y: 8.6f, z: 10f))),
            ],
        };
    }
    // Stages the volume until its trace lands, returning the parameters and the last words it wrote.
    private static (SdfMeshIrradianceParameters Parameters, uint[] Words) Trace(SdfMeshIrradiance volume, SdfFrame frame, SdfIndirectTier tier = SdfIndirectTier.High) {
        var words = Array.Empty<uint>();
        var parameters = default(SdfMeshIrradianceParameters);
        var deadline = DateTime.UtcNow.AddSeconds(value: 120d);

        do {
            var wrote = false;

            parameters = volume.Stage(bakePending: false, frame: frame, tier: tier, write: memory => {
                wrote = true;
                words = memory.ToArray();
            });
            // A publication holds every slot of every level it publishes, each its own cell's probe.
            if (wrote) {
                var first = 0;

                for (var level = 0; (level < SdfMeshIrradianceProbe.Levels); level++) {
                    var window = parameters.Window(level: level);

                    for (var z = window.Low.Z; window.Present && (z < (window.Low.Z + window.Dims.Z)); z++) {
                        for (var y = window.Low.Y; (y < (window.Low.Y + window.Dims.Y)); y++) {
                            for (var x = window.Low.X; (x < (window.Low.X + window.Dims.X)); x++) {
                                var slot = (first + SdfMeshIrradiance.SlotOf(dims: window.Dims, x: x, y: y, z: z));

                                Assert.Equal(expected: SdfMeshIrradiance.KeyOf(x: x, y: y, z: z) | SdfMeshIrradianceProbe.WrittenFlag,
                                    actual: words[(slot * SdfMeshIrradianceProbe.ProbeWords)] & ~SdfMeshIrradianceProbe.InvalidFlag);
                            }
                        }
                    }
                    first += window.Count;
                }
            }
            Thread.Sleep(millisecondsTimeout: 20);
        } while (volume.Settling && (DateTime.UtcNow < deadline));

        Assert.False(condition: volume.Settling);
        Assert.True(condition: (words.Length > 0), userMessage: volume.Describe());

        return (parameters, words);
    }
    private static ReadOnlySpan<uint> Probe(uint[] words, in SdfMeshIrradianceParameters parameters, int x, int y, int z) {
        var slot = SdfMeshIrradiance.SlotOf(dims: parameters.Finest.Dims, x: x, y: y, z: z);
        var probe = words.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (slot * SdfMeshIrradianceProbe.ProbeWords));

        Assert.Equal(expected: SdfMeshIrradiance.KeyOf(x: x, y: y, z: z) | SdfMeshIrradianceProbe.WrittenFlag, actual: probe[0]);

        return probe;
    }
    private static float Half(uint word, bool high) => ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(high ? (word >> 16) : word & 0xffffu))));
    private static float Luminance(ReadOnlySpan<uint> probe, int face) {
        var sum = 0f;

        for (var channel = 0; (channel < 3); channel++) {
            var entry = ((3 * face) + channel);

            sum += Half(high: ((entry & 1) != 0), word: probe[(4 + (entry >> 1))]);
        }

        return sum;
    }

    [Fact]
    public void AResizedOrMovedWindowPublishesOnlyAfterItsCellsHaveMeasurements() {
        var frame = Frame() with { IndirectBounces = 0 };
        var volume = Volume();

        _ = Trace(frame: frame, volume: volume);
        var (resized, _) = Trace(frame: frame, tier: SdfIndirectTier.Medium, volume: volume);

        Assert.Equal(expected: new Vector3(value: 2f), actual: resized.Finest.Spacing);
        var view = frame.Views[0];

        SdfFrame At(float x) => frame with {
            Views = [view with {
            Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: x, y: 2f, z: 0f),
                target: new Vector3(x: (x + 1f), y: 2f, z: 0f), viewportHeight: 4u, viewportWidth: 4u),
        }],
        };
        // A camera still in the window's middle keeps it; one past it recentres the window on its cell.
        var kept = volume.Stage(bakePending: false, frame: At(x: 6f), tier: SdfIndirectTier.Medium, write: static _ => { });

        var (moved, _) = Trace(frame: At(x: 30f), tier: SdfIndirectTier.Medium, volume: volume);

        Assert.Equal(expected: resized.Finest.Low, actual: kept.Finest.Low);
        Assert.Equal(expected: (resized.Finest.Low.X + 15), actual: moved.Finest.Low.X);
    }
    /// <summary>The finest window holds its first camera in its middle, an eighth of the window short of each face, though
    /// it centres a quarter of its extent ahead of the camera: a camera walking backward, straight or sideways, moves the
    /// window before it nears the back face, and a camera turning in place moves it only as its anchor leaves the middle,
    /// about every third of a turn.</summary>
    [Theory]
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    public void TheFinestWindowKeepsItsCameraInItsMiddle(SdfIndirectTier tier) {
        const BindingFlags Private = (BindingFlags.Instance | BindingFlags.NonPublic);
        var finest = SdfMeshIrradiance.LevelsOf(tier: tier)[0];
        var volume = Volume();
        var layout = typeof(SdfMeshIrradiance).GetNestedType(name: "Layout", bindingAttr: BindingFlags.NonPublic)!.GetMethod(name: "Of", bindingAttr: (BindingFlags.Static | BindingFlags.Public))!.Invoke(obj: null, parameters: [tier, 0, 60f]);
        var windowLow = typeof(SdfMeshIrradiance).GetMethod(name: "WindowLow", bindingAttr: Private)!;
        var camera = typeof(SdfMeshIrradiance).GetField(name: "m_camera", bindingAttr: Private)!;
        var view = Frame().Views[0];
        var low = default((int X, int Y, int Z));
        var moves = 0;

        void Step(Vector3 position, Vector3 forward, bool resizing) {
            camera.SetValue(obj: volume, value: position);

            var frame = Frame() with { Views = [view with { Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: position, target: (position + forward), viewportHeight: 4u, viewportWidth: 4u) }] };
            var next = ((int X, int Y, int Z))windowLow.Invoke(obj: volume, parameters: [frame, 0, layout, low, resizing])!;

            moves += ((next != low) ? 1 : 0);
            low = next;

            int[] cell = [(int)MathF.Floor(x: (position.X / finest.Spacing.X)), (int)MathF.Floor(x: (position.Y / finest.Spacing.Y)), (int)MathF.Floor(x: (position.Z / finest.Spacing.Z))];
            int[] first = [low.X, low.Y, low.Z];
            int[] dims = [finest.Dims.X, finest.Dims.Y, finest.Dims.Z];

            for (var axis = 0; (axis < 3); axis++) {
                var margin = Math.Max(val1: 1, val2: (dims[axis] / 8));

                Assert.InRange(actual: cell[axis], low: (first[axis] + margin), high: ((first[axis] + dims[axis]) - margin - 1));
            }
        }

        Step(position: new Vector3(x: 0f, y: 2f, z: 0f), forward: Vector3.UnitX, resizing: true);
        for (var step = 1; (step <= 400); step++) {
            Step(position: new Vector3(x: (-0.25f * step), y: 2f, z: (-0.1f * step)), forward: Vector3.UnitX, resizing: false);
        }
        moves = 0;
        for (var step = 0; (step <= 72); step++) {
            var angle = (step * (MathF.PI / 36f));

            Step(position: new Vector3(x: -100f, y: 2f, z: -40f), forward: new Vector3(x: MathF.Cos(x: angle), y: 0f, z: MathF.Sin(x: angle)), resizing: false);
        }
        Assert.InRange(actual: moves, low: 1, high: 4);
    }
    // A first view another residency renders (a seat crossed into another world, its camera in that world's coordinates)
    // holds the window where it stood and meters no exposure; the window follows the view again once this residency
    // renders it.
    [Fact]
    public void AWindowHoldsWhileItsFirstViewRendersElsewhereAndFollowsItBack() {
        var frame = Frame() with { IndirectBounces = 0 };
        var volume = Volume();

        var (here, _) = Trace(frame: frame, tier: SdfIndirectTier.Medium, volume: volume);
        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 40f, y: 2f, z: 0f),
            target: new Vector3(x: 41f, y: 2f, z: 0f), viewportHeight: 4u, viewportWidth: 4u);
        var elsewhere = frame with { Views = [frame.Views[0] with { Camera = camera, Elsewhere = true }] };
        var held = volume.Stage(bakePending: false, frame: elsewhere, tier: SdfIndirectTier.Medium, write: static _ => { });
        var exposures = new SdfViewExposures();

        exposures.Stage(frame: elsewhere, irradiance: volume, seconds: 0.1d);
        // A view meters on its first read in the frame.
        _ = exposures.MultiplierOf(view: 0);

        Assert.Equal(expected: here.Finest.Low, actual: held.Finest.Low);
        Assert.Contains(expectedSubstring: "source=elsewhere", actualString: exposures.Describe()[0]);

        var (back, _) = Trace(frame: (elsewhere with { Views = [elsewhere.Views[0] with { Elsewhere = false }] }), tier: SdfIndirectTier.Medium, volume: volume);

        Assert.Equal(expected: (here.Finest.Low.X + 20), actual: back.Finest.Low.X);
    }
    /// <summary>A lighting-converged frame waits for its scene and window's trace and relights the window from no bounce until it
    /// converges, in one staging, including a cold scene and a replacement: its probes are a function of the scene,
    /// lighting and window alone, the same on a fresh volume as on one whose asynchronous frames left other bounce.</summary>
    [Fact]
    public void AConvergedLightingFrameRendersTheWindowTracedToTheEnd() {
        var frame = (Frame() with { ConvergesLighting = true });
        var fresh = Volume();
        var landed = Array.Empty<uint>();

        _ = fresh.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: memory => landed = memory.ToArray());

        var locked = Volume();
        var writes = 0;
        var words = Array.Empty<uint>();

        _ = Trace(frame: (frame with { ConvergesLighting = false }), volume: locked);
        _ = locked.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: memory => {
            writes++;
            words = memory.ToArray();
        });

        Assert.Equal(actual: writes, expected: 1);
        Assert.Equal(actual: words, expected: landed);

        _ = locked.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => writes++);
        Assert.Equal(actual: writes, expected: 1);

        var changed = frame with { MeshDraws = [frame.MeshDraws[0], frame.MeshDraws[2]] };
        var replacement = Array.Empty<uint>();

        _ = Volume().Stage(bakePending: false, frame: changed, tier: SdfIndirectTier.High, write: memory => replacement = memory.ToArray());
        var tracedBefore = locked.Counters.Read(kind: SdfMeshIrradiance.Traced);

        _ = locked.Stage(bakePending: false, frame: changed, tier: SdfIndirectTier.High, write: memory => {
            writes++;
            words = memory.ToArray();
        });
        Assert.Equal(actual: writes, expected: 2);
        Assert.Equal(actual: words, expected: replacement);
        Assert.NotEqual(actual: words, expected: landed);
        // Removing the slab retraces only the probes it reached; the rest keep hits a trace would reproduce.
        Assert.InRange(actual: (locked.Counters.Read(kind: SdfMeshIrradiance.Traced) - tracedBefore), low: 1L, high: (SdfMeshIrradiance.MaxProbes - 1L));
    }
    /// <summary>A lockstep frame's exposure adapts over its own presentation time since the frame staged before it, never
    /// backward and nothing for the first; any other frame over the host frame's interval.</summary>
    [Fact]
    public void ALockstepFrameAdaptsExposureOverItsOwnTime() {
        var frame = (Frame() with { TickLocked = true, Time = 2.5f });

        Assert.Equal(expected: 0.5d, actual: SdfViewExposures.SecondsOf(frame: frame, frameSeconds: 0.016d, previousTime: 2f), precision: 6);
        Assert.Equal(expected: 0d, actual: SdfViewExposures.SecondsOf(frame: frame, frameSeconds: 0.016d, previousTime: float.NaN));
        Assert.Equal(expected: 0d, actual: SdfViewExposures.SecondsOf(frame: frame, frameSeconds: 0.016d, previousTime: 3f));
        Assert.Equal(expected: 0.016d, actual: SdfViewExposures.SecondsOf(frame: (frame with { TickLocked = false }), frameSeconds: 0.016d, previousTime: 2f));
    }
    [Fact]
    public void AStaticPointLightIsSeenAndBouncedInItsOwnRoomAlone_AndDepthStopsAtTheSlab() {
        var volume = Volume();
        var frame = Frame();

        var (parameters, words) = Trace(frame: frame, volume: volume);

        Assert.True(condition: parameters.Enabled);
        Assert.Equal(expected: Vector3.One, actual: parameters.Finest.Spacing);

        // The probe at (0.5, 5.5, 0.5), beside the light in the upper room, and the one at (0.5, 1.5, 0.5) under the slab.
        var upper = Probe(parameters: in parameters, words: words, x: 0, y: 5, z: 0);
        var lower = Probe(parameters: in parameters, words: words, x: 0, y: 1, z: 0);

        Assert.Equal(expected: 1u, actual: upper[SdfMeshIrradianceProbe.LightsWord] & 1u);
        Assert.Equal(expected: 0u, actual: lower[SdfMeshIrradianceProbe.LightsWord] & 1u);
        // The slab's lit top sends light up to the upper probe's -Y face; the floor under the slab sends the lower
        // probe's only what spills round the slab's open edges and bounces back in, under a hundredth of it.
        var upperLuminance = Luminance(face: 3, probe: upper);

        Assert.True(condition: (upperLuminance > 0f));
        Assert.True(condition: (Luminance(face: 3, probe: lower) < (0.01f * upperLuminance)));
        var moments = lower.Slice(length: SdfMeshIrradianceProbe.DepthWords, start: SdfMeshIrradianceProbe.DepthWord);

        Assert.True(condition: (SdfMeshProbeDepth.Visibility(surfaces: moments, toward: new Vector3(x: 0.3f, y: 2.8f, z: -0.2f)) < 0.001f));
        Assert.Equal(1f, SdfMeshProbeDepth.Visibility(surfaces: moments, toward: new Vector3(x: 0.3f, y: 2.4f, z: -0.2f)));
        // The meter reads the lit room lit and the dark room dark.
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var lit, position: new Vector3(x: 0.5f, y: 5.5f, z: 0.5f), skyVisibility: out _, source: out _));
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var dark, position: new Vector3(x: 0.5f, y: 1.5f, z: 0.5f), skyVisibility: out _, source: out _));
        Assert.True(condition: (lit > 0f));
        Assert.True(condition: (dark < (0.01f * lit)));

        // The camera can approach the slab without reading the lit probes beyond it.
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var nearSlab, position: new Vector3(x: 0.5f, y: 3.99f, z: 0.5f), skyVisibility: out _, source: out _));
        Assert.True(condition: (nearSlab < (0.01f * lit)));

        var (_, unlitWords) = Trace(frame: frame with { Lights = new SdfLights() }, volume: volume);
        var unlitUpper = Probe(parameters: in parameters, words: unlitWords, x: 0, y: 5, z: 0);

        // Bounce the lit room left in the cache decays over the relights that converge it, to under a thousandth.
        Assert.True(condition: (Luminance(face: 3, probe: unlitUpper) < (0.001f * upperLuminance)), userMessage: $"unlit={Luminance(face: 3, probe: unlitUpper)} lit={upperLuminance}");
        Assert.Equal(expected: 0u, actual: unlitUpper[SdfMeshIrradianceProbe.LightsWord]);
    }
    [Fact]
    public void ASpaceBetweenLatticeCentresMetersItsOwnLightingWithoutOpeningTheWalls() {
        var frame = Frame();
        var lights = new SdfLights();

        lights.Set(index: 0, light: frame.Lights.Records[0] with { Direction = new Vector3(x: 0f, y: 2.8f, z: 0f) });
        lights.Count = 1;
        frame = frame with {
            Lights = lights,
            IndirectBounces = 0,
            ConvergesLighting = true,
            Exposure = new SdfExposure(Adaptation: 0f, Compensation: 0f, Ev100: 15f, Local: 0.5f, Maximum: 20f, Minimum: 0f, Mode: SdfExposureMode.Auto),
            // An 18-cm gap contains the camera and light but no lattice centres (x = +/- 0.5), and is narrower than twice
            // the clearance, so no probe of the cells about it can move into it either.
            MeshDraws = [.. frame.MeshDraws,
                Draw(mesh: Box(low: new Vector3(x: -10f, y: 0f, z: -10f), high: new Vector3(x: -0.09f, y: 4f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: 0.09f, y: 0f, z: -10f), high: new Vector3(x: 10f, y: 4f, z: 10f))),
            ],
        };
        var volume = Volume();

        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => { });
        Assert.True(condition: volume.TryMeter(position: frame.Views[0].Camera.Position, forward: Vector3.UnitX,
            luminance: out var lit, skyVisibility: out var sky, source: out var source));
        Assert.Equal(actual: source, expected: SdfExposureMeterSource.CameraTrace);
        Assert.True(condition: (lit > 0f));
        Assert.InRange(actual: sky, high: 0.1f, low: 0f);
        var traces = volume.Counters.Read(kind: SdfMeshIrradiance.CameraTraces);

        for (var read = 0; (read < 100); read++) {
            Assert.True(condition: volume.TryMeter(position: frame.Views[0].Camera.Position, forward: Vector3.UnitX,
                luminance: out var repeated, skyVisibility: out _, source: out _));
            Assert.Equal(actual: repeated, expected: lit);
        }
        Assert.True(condition: volume.TryMeter(position: (frame.Views[0].Camera.Position + new Vector3(x: 0f, y: 0.05f, z: 0f)),
            forward: Vector3.UnitY, luminance: out _, skyVisibility: out _, source: out _));
        Assert.Equal(expected: traces, actual: volume.Counters.Read(kind: SdfMeshIrradiance.CameraTraces));
        var exposures = new SdfViewExposures();

        exposures.Stage(frame: frame, irradiance: Volume(), seconds: 1d);
        Assert.Equal(expected: SdfPhotometry.ExposureOf(ev100: frame.Exposure.Ev100), actual: exposures.MultiplierOf(view: 0));
        Assert.Contains(expectedSubstring: "meter=none source=initial", actualString: exposures.Describe()[0]);
        exposures.Stage(frame: frame, irradiance: volume, seconds: 1d);
        Assert.Equal(expected: SdfPhotometry.ExposureOf(ev100: frame.Exposure.Target(luminance: lit)), actual: exposures.MultiplierOf(view: 0), precision: 6);
        Assert.Contains(expectedSubstring: "source=camera-trace", actualString: exposures.Describe()[0]);
        var held = exposures.MultiplierOf(view: 0);
        var heldAir = exposures.AirLightOf(view: 0);

        exposures.Stage(frame: frame, irradiance: Volume(), seconds: 1d);
        Assert.Equal(expected: held, actual: exposures.MultiplierOf(view: 0));
        Assert.Equal(expected: heldAir, actual: exposures.AirLightOf(view: 0));
        Assert.Equal(expected: lit, actual: exposures.MeteredOf(view: 0));
        Assert.Contains(expectedSubstring: "meter=none source=held", actualString: exposures.Describe()[0]);
        // Inside the opaque wall is unknown, not a reading of an unlit room.
        Assert.False(condition: volume.TryMeter(position: new Vector3(x: 0.3f, y: 2f, z: 0f), forward: Vector3.UnitX,
            luminance: out _, skyVisibility: out _, source: out source));
        Assert.Equal(actual: source, expected: SdfExposureMeterSource.None);

        frame = frame with { Lights = new SdfLights() };
        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => { });
        Assert.True(condition: volume.TryMeter(position: frame.Views[0].Camera.Position, forward: Vector3.UnitX,
            luminance: out var unlit, skyVisibility: out _, source: out source));
        Assert.Equal(actual: source, expected: SdfExposureMeterSource.CameraTrace);
        Assert.Equal(actual: unlit, expected: 0f);
        Assert.True(condition: (volume.Counters.Read(kind: SdfMeshIrradiance.CameraTraces) > traces));
    }
    [Fact]
    public void AnOpeningBesideAnAxisDoesNotEraseItsFirstBlocker() {
        var frame = Frame() with {
            IndirectBounces = 0,
            MeshDraws = [Draw(mesh: Box(low: new Vector3(x: 1f, y: 0.4f, z: 0.4f), high: new Vector3(x: 1.1f, y: 0.6f, z: 0.6f)))],
        };

        var (parameters, words) = Trace(volume: Volume(), frame: frame);
        var probe = Probe(parameters: in parameters, words: words, x: 0, y: 0, z: 0);
        var depth = probe.Slice(length: SdfMeshIrradianceProbe.DepthWords, start: SdfMeshIrradianceProbe.DepthWord);

        Assert.True(condition: (SdfMeshProbeDepth.Visibility(surfaces: depth, toward: new Vector3(x: 1f, y: 0f, z: 0f)) < 0.05f));
        Assert.Equal(1f, SdfMeshProbeDepth.Visibility(surfaces: depth, toward: new Vector3(x: 0.4f, y: 0f, z: 0f)));
    }

    // A long sunlit floor under a camera at its west end, beyond the finest window's reach.
    private static SdfFrame OpenFloor() {
        var lights = new SdfLights();

        lights.Set(index: 0, light: new SdfLight(Color: Vector3.One, Direction: Vector3.Normalize(value: new Vector3(x: 0.3f, y: 1f, z: 0.2f)), Kind: SdfLightKind.Directional, Param: 0f, Shadows: true, Weight: 1000f));
        lights.Count = 1;
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);

        return Frame() with {
            FarDistance = 400f,
            IndirectBounces = 0,
            Lights = lights,
            MeshDraws = [Draw(mesh: Box(low: new Vector3(x: -10f, y: -0.3f, z: -20f), high: new Vector3(x: 300f, y: 0f, z: 20f)))],
        };
    }
    /// <summary>THE LAW: a surface past the finest window reads a coarser level's measured light, never the absence of a
    /// measurement. On a sunlit floor whose camera stands 60 m away, the floor's top reads the second level's probes as
    /// measured, with weight; one past every window still reads its nearest probes' light, unmeasured, and a traced
    /// light's visibility there is zero.</summary>
    [Fact]
    public void ASurfacePastTheFinestWindowReadsACoarserLevelsMeasuredLight() {
        var volume = Volume();
        var (parameters, words) = Trace(frame: OpenFloor() with { ConvergesLighting = true }, tier: SdfIndirectTier.Medium, volume: volume);

        Assert.True(condition: parameters.Middle.Present && parameters.Far.Present);
        Assert.Equal(expected: new Vector3(x: 4f, y: 2f, z: 4f), actual: parameters.Middle.Spacing);

        var far = volume.DescribeProbes(point: new Vector3(x: 60f, y: 0f, z: 0f), normal: Vector3.UnitY)[0];

        Assert.Contains(expectedSubstring: "level=1 measured=1", actualString: far);
        Assert.DoesNotContain(expectedSubstring: "weight=0]", actualString: far);

        var beyond = volume.DescribeProbes(point: new Vector3(x: 2000f, y: 0f, z: 0f), normal: Vector3.UnitY)[0];

        Assert.Contains(expectedSubstring: "level=2 measured=0", actualString: beyond);
        Assert.DoesNotContain(expectedSubstring: "weight=0]", actualString: beyond);
        Assert.Equal(expected: 0f, actual: volume.LightVisibility(lightIndex: 0, normal: Vector3.UnitY, point: new Vector3(x: 2000f, y: 0f, z: 0f)));
        Assert.True(condition: (words.Length == ((parameters.Window(level: 0).Count + parameters.Middle.Count + parameters.Far.Count) * SdfMeshIrradianceProbe.ProbeWords)));
    }
    /// <summary>THE LAW: a finer level's probes answer a receiver past their window as far as their surface maps reach,
    /// before a coarser level does. At the High tier the finest window about a camera looking at the origin from a quarter
    /// of the window behind it ends half a metre short of a room's end wall, whose metres-wide coarse taps cannot resolve
    /// it. The wall reads the finest level's
    /// boundary probes, measured, with weight; a floor far past the finest probes' reach still reads the coarser level.
    /// The inner wall of a sealed room past that reach reads light rather than black.</summary>
    [Fact]
    public void AReceiverJustPastTheFinestWindowReadsTheFinestProbesThatMeasuredIt() {
        const float EndWall = 24.3f;
        var volume = Volume();
        var room = Frame() with {
            ConvergesLighting = true,
            Views = [Frame().Views[0] with { Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: -12f, y: 2f, z: 0f), target: new Vector3(x: -11f, y: 2f, z: 0f), viewportHeight: 4u, viewportWidth: 4u) }],
            MeshDraws = [
                Draw(mesh: Box(low: new Vector3(x: -10f, y: -0.3f, z: -10f), high: new Vector3(x: (EndWall + 0.3f), y: 0f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: -10f, y: 4f, z: -10f), high: new Vector3(x: (EndWall + 0.3f), y: 4.3f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: EndWall, y: 0f, z: -10f), high: new Vector3(x: (EndWall + 0.3f), y: 4f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: -300f, y: -0.6f, z: -300f), high: new Vector3(x: 300f, y: -0.3f, z: 300f))),
                // A sealed room 3 m on a side, 60 m out: a floor, a roof and four walls.
                Draw(mesh: Box(low: new Vector3(x: 60f, y: -0.3f, z: 0f), high: new Vector3(x: 63.3f, y: 0f, z: 3.3f))),
                Draw(mesh: Box(low: new Vector3(x: 60f, y: 3f, z: 0f), high: new Vector3(x: 63.3f, y: 3.3f, z: 3.3f))),
                Draw(mesh: Box(low: new Vector3(x: 60f, y: 0f, z: 0f), high: new Vector3(x: 60.3f, y: 3f, z: 3.3f))),
                Draw(mesh: Box(low: new Vector3(x: 63f, y: 0f, z: 0f), high: new Vector3(x: 63.3f, y: 3f, z: 3.3f))),
                Draw(mesh: Box(low: new Vector3(x: 60.3f, y: 0f, z: 0f), high: new Vector3(x: 63f, y: 3f, z: 0.3f))),
                Draw(mesh: Box(low: new Vector3(x: 60.3f, y: 0f, z: 3f), high: new Vector3(x: 63f, y: 3f, z: 3.3f))),
            ],
        };
        var (parameters, _) = Trace(frame: room, tier: SdfIndirectTier.High, volume: volume);
        var finest = parameters.Window(level: 0);
        var point = new Vector3(x: EndWall, y: 2f, z: 0.5f);
        var beyond = ((point.X - SdfMeshIrradianceProbe.NormalBias) - ((finest.Low.X + finest.Dims.X - 0.5f) * finest.Spacing.X));

        Assert.InRange(actual: beyond, low: 0.01f, high: SdfMeshIrradiance.SurfaceReachOf(spacing: finest.Spacing));

        var wall = volume.DescribeProbes(point: point, normal: -Vector3.UnitX)[0];

        Assert.Contains(expectedSubstring: "level=0 measured=1", actualString: wall);
        Assert.DoesNotContain(expectedSubstring: "weight=0]", actualString: wall);
        Assert.Contains(expectedSubstring: "level=1 measured=1", actualString: volume.DescribeProbes(point: new Vector3(x: 50f, y: -0.3f, z: 0f), normal: Vector3.UnitY)[0]);

        var sealedWall = volume.DescribeProbes(point: new Vector3(x: 63f, y: 1.5f, z: 1.65f), normal: -Vector3.UnitX)[0];

        Assert.DoesNotContain(expectedSubstring: "weight=0]", actualString: sealedWall);
    }
    /// <summary>THE LAW: the finest level hands a receiver past its window to the next stage across the outer half of its
    /// reach, not at one line. Along a sunlit floor at the High tier, from the finest window's boundary probes out past
    /// their reach every 5 cm, the finest level's share of the answer falls from whole to none and never steps by more than
    /// a fifth between neighbours, and the receivers past the reach read the storey level.</summary>
    [Fact]
    public void TheFinestLevelHandsAReceiverPastItsWindowOverAcrossItsReach() {
        var volume = Volume();
        var (parameters, _) = Trace(frame: OpenFloor() with { ConvergesLighting = true }, tier: SdfIndirectTier.High, volume: volume);
        var finest = parameters.Window(level: 0);
        var boundary = ((finest.Low.X + finest.Dims.X - 0.5f) * finest.Spacing.X);
        var reach = SdfMeshIrradiance.SurfaceReachOf(spacing: finest.Spacing);
        var cells = new (int X, int Y, int Z)[SdfMeshIrradiance.GatherSlots];
        var weights = new float[SdfMeshIrradiance.GatherSlots];
        var previous = 1f;
        var steepest = 0f;

        for (var x = boundary; (x < (boundary + reach + 0.5f)); x += 0.05f) {
            Assert.True(condition: volume.TryGather(cells: cells, normal: Vector3.UnitY, point: new Vector3(x: x, y: 0f, z: 0.3f), reports: default, selection: out var selection, weights: weights));

            var share = ((selection.Level == 0) ? selection.Share : 0f);

            steepest = MathF.Max(x: steepest, y: MathF.Abs(x: (share - previous)));
            previous = share;
            if (x > (boundary + reach + 0.1f)) { Assert.Equal(expected: 1, actual: selection.Level); }
        }
        Assert.Equal(expected: 0f, actual: previous);
        Assert.True(condition: (steepest <= 0.2f), userMessage: $"the finest level's share steps by {steepest} between receivers 5 cm apart");
    }
    /// <summary>THE LAW: the meter answers while the bake still lands static meshes: a frame with the bake pending builds
    /// a scene from the meshes it has, the probes trace it, and the meter reads; a pending bake with no static mesh landed
    /// builds no scene, since an empty one would measure open sky where walls have yet to land; a lockstep frame builds
    /// none while its bake is pending.</summary>
    [Fact]
    public void TheMeterAnswersWhileTheBakeStillLands() {
        var lit = Frame();
        // In the upper room, under its point light.
        var frame = lit with {
            IndirectBounces = 0,
            Views = [lit.Views[0] with { Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 0.5f, y: 5.5f, z: 0.5f), target: new Vector3(x: 1.5f, y: 5.5f, z: 0.5f), viewportHeight: 4u, viewportWidth: 4u) }],
        };
        var volume = Volume();
        var deadline = DateTime.UtcNow.AddSeconds(value: 120d);

        do {
            _ = volume.Stage(bakePending: true, frame: frame, tier: SdfIndirectTier.Medium, write: static _ => { });
            Thread.Sleep(millisecondsTimeout: 20);
        } while (volume.Settling && (DateTime.UtcNow < deadline));

        Assert.NotNull(@object: volume.DiagnosticScene(frame: frame));
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var luminance, position: frame.Views[0].Camera.Position, skyVisibility: out _, source: out var source));
        Assert.Equal(expected: SdfExposureMeterSource.Probes, actual: source);
        Assert.True(condition: (luminance > 0f));

        var bare = Volume();

        _ = bare.Stage(bakePending: true, frame: (frame with { MeshDraws = [] }), tier: SdfIndirectTier.Medium, write: static _ => { });
        Assert.Null(@object: bare.DiagnosticScene(frame: (frame with { MeshDraws = [] })));
        Assert.False(condition: bare.TryMeter(forward: Vector3.UnitX, luminance: out _, position: frame.Views[0].Camera.Position, skyVisibility: out _, source: out _));

        var locked = Volume();

        _ = locked.Stage(bakePending: true, frame: (frame with { ConvergesLighting = true }), tier: SdfIndirectTier.Medium, write: static _ => { });
        Assert.Null(@object: locked.DiagnosticScene(frame: frame));
    }
    /// <summary>THE LAW: a camera cut snaps to the first reading after it. A cut the meter cannot read yet stays owed,
    /// so the frame that first reads takes its target at once rather than easing from the exposure before the cut.</summary>
    [Fact]
    public void ACutSnapsToTheFirstReadingAfterIt() {
        var frame = Frame() with {
            IndirectBounces = 0,
            ConvergesLighting = true,
            Exposure = new SdfExposure(Adaptation: 0.75f, Compensation: 0f, Ev100: 15f, Local: 0f, Maximum: 20f, Minimum: 0f, Mode: SdfExposureMode.Auto),
        };
        var volume = Volume();

        _ = Trace(frame: frame, tier: SdfIndirectTier.Medium, volume: volume);
        Assert.True(condition: volume.TryMeter(forward: frame.Views[0].Camera.Forward, luminance: out var lit, position: frame.Views[0].Camera.Position, skyVisibility: out _, source: out _));

        var exposures = new SdfViewExposures();
        var cut = frame with { Views = [frame.Views[0] with { CutRevision = 1 }] };

        exposures.Stage(frame: frame, irradiance: Volume(), seconds: 0.01d);
        exposures.Stage(frame: cut, irradiance: Volume(), seconds: 0.01d);
        Assert.Contains(expectedSubstring: "meter=none", actualString: exposures.Describe()[0]);
        exposures.Stage(frame: cut, irradiance: volume, seconds: 0.01d);
        Assert.Equal(expected: SdfPhotometry.ExposureOf(ev100: frame.Exposure.Target(luminance: lit)), actual: exposures.MultiplierOf(view: 0), precision: 6);
    }
}
