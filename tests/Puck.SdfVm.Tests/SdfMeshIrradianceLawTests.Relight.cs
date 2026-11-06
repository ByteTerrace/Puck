using System.Numerics;
using System.Reflection;
using Puck.Abstractions.Cameras;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The relight's laws: the budget never leaves the worker spinning and a lockstep frame takes over its work;
/// the bounce converges over the whole window, colour included; a camera sample relights from where it was traced; and
/// a dim colour channel of the sky still relights.</summary>
public sealed partial class SdfMeshIrradianceLawTests {
    private static Vector3 Radiance(ReadOnlySpan<uint> probe, int face) {
        Span<float> channels = stackalloc float[3];

        for (var channel = 0; (channel < 3); channel++) {
            var entry = ((3 * face) + channel);

            channels[channel] = Half(high: ((entry & 1) != 0), word: probe[(4 + (entry >> 1))]);
        }
        return new Vector3(x: channels[0], y: channels[1], z: channels[2]);
    }
    private static Vector3 Cube(ReadOnlySpan<uint> probe) {
        var sum = Vector3.Zero;

        for (var face = 0; (face < 6); face++) { sum += Radiance(face: face, probe: probe); }
        return sum;
    }
    private static SdfLights Lamp(Vector3 position, Vector3 color) {
        var lights = new SdfLights();

        lights.Set(index: 0, light: new SdfLight(Color: color, Direction: position, Kind: SdfLightKind.Point, Param: 40f, Shadows: true, Weight: SdfPhotometry.PositionalWeight(candela: 20000f)));
        lights.Count = 1;
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);
        return lights;
    }
    private static uint[] Locked(SdfFrame frame, SdfIndirectTier tier) {
        var words = Array.Empty<uint>();

        using var volume = new SdfMeshIrradiance();

        _ = volume.Stage(bakePending: false, frame: (frame with { ConvergesLighting = true }), tier: tier, write: memory => words = memory.ToArray());
        return words;
    }

    /// <summary>A budget of one trace a frame, spent, leaves the worker waiting for the next grant rather than listing
    /// the unfunded window again and again under either presentation clock; an explicit lighting-converged frame then
    /// takes over what it had left and renders the window a fresh converged frame does.</summary>
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ASpentTraceBudgetWaitsUnderEitherClockUntilLightingConverges(bool tickLocked) {
        var frame = Frame() with { TickLocked = tickLocked, IndirectBounces = 0, IndirectBudget = new SdfIndirectBudget(Relight: 0, Trace: 1, Age: 0f) };
        var volume = Volume();

        for (var stage = 0; (stage < 20); stage++) {
            _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.Medium, write: _ => { });
            Thread.Sleep(millisecondsTimeout: 20);
        }
        // Each frame grants one trace, held to two frames' worth: twenty frames fund at most 21.
        Assert.InRange(actual: volume.Counters.Read(kind: SdfMeshIrradiance.Traced), low: 1L, high: 21L);

        var words = Array.Empty<uint>();
        var locked = Task.Run(() => volume.Stage(bakePending: false, frame: (frame with { ConvergesLighting = true }), tier: SdfIndirectTier.Medium, write: memory => words = memory.ToArray()), TestContext.Current.CancellationToken);

        Assert.True(condition: (await Task.WhenAny(locked, Task.Delay(delay: TimeSpan.FromSeconds(value: 120d), cancellationToken: TestContext.Current.CancellationToken)) == locked), userMessage: "the lockstep frame waited on a worker that never yielded");
        Assert.Equal(expected: Locked(frame: frame, tier: SdfIndirectTier.Medium), actual: words);
    }

    // Four white rooms in a row, 6 m each, joined by openings at alternate sides of the baffles between them, so no
    // straight line reaches the first room from the lamp in the last: the first room is lit by bounce alone, around two
    // baffles. The camera stands in the first room, so its probes are relit first.
    private static SdfFrame BaffledRooms() {
        var frame = Frame();
        var view = frame.Views[0];
        SdfMeshDraw Slab(float x0, float y0, float z0, float x1, float y1, float z1) => Draw(mesh: Box(high: new Vector3(x: x1, y: y1, z: z1), low: new Vector3(x: x0, y: y0, z: z0)));

        return frame with {
            IndirectBounces = 3,
            Lights = Lamp(color: Vector3.One, position: new Vector3(x: 22f, y: 1.5f, z: 3f)),
            MeshDraws = [
                Slab(-0.3f, -0.3f, -0.3f, 24.3f, 0f, 6.3f),
                Slab(-0.3f, 3f, -0.3f, 24.3f, 3.3f, 6.3f),
                Slab(-0.3f, 0f, -0.3f, 24.3f, 3f, 0f),
                Slab(-0.3f, 0f, 6f, 24.3f, 3f, 6.3f),
                Slab(-0.3f, 0f, 0f, 0f, 3f, 6f),
                Slab(24f, 0f, 0f, 24.3f, 3f, 6f),
                Slab(6f, 0f, 1.5f, 6.3f, 3f, 6f),
                Slab(12f, 0f, 0f, 12.3f, 3f, 4.5f),
                Slab(18f, 0f, 1.5f, 18.3f, 3f, 6f),
            ],
            Views = [view with {
                Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 2f, y: 1.5f, z: 3f), target: new Vector3(x: 3f, y: 1.5f, z: 3f), viewportHeight: 4u, viewportWidth: 4u),
            }],
        };
    }

    /// <summary>The bounce converges over the whole window, not probe by probe: the first of the baffled rooms, whose
    /// probes are relit before the light reaches them, still takes the lamp's bounce, in a lockstep frame and in the
    /// asynchronous worker alike.</summary>
    [Fact]
    public void BounceAroundBafflesReachesTheFarRoomWhoseProbesWereRelitFirst() {
        var frame = BaffledRooms();
        var (parameters, landed) = Trace(frame: frame, tier: SdfIndirectTier.Medium, volume: Volume());
        var locked = Locked(frame: frame, tier: SdfIndirectTier.Medium);
        // The cell about (3, 1, 3) m, in the first room.
        var far = Cube(probe: Probe(parameters: in parameters, words: landed, x: 1, y: 0, z: 1));
        var lockedFar = Cube(probe: Probe(parameters: in parameters, words: locked, x: 1, y: 0, z: 1));
        var lit = Cube(probe: Probe(parameters: in parameters, words: locked, x: 10, y: 0, z: 1));

        Assert.True(condition: (lockedFar.Y > (1.0e-4f * lit.Y)), userMessage: $"far room {lockedFar} beside the lamp's room {lit}");
        Assert.True(condition: (MathF.Abs(x: (far.Y - lockedFar.Y)) <= (0.1f * lockedFar.Y)), userMessage: $"asynchronous {far} against lockstep {lockedFar}");
    }

    /// <summary>A red lamp turned green at the same luminance relights the room green: the bounce's red, which a luminance
    /// bound alone would leave behind, converges away as a fresh volume under the green lamp never had it.</summary>
    [Fact]
    public void ALampTurnedGreenAtTheSameLuminanceLeavesNoRedBounce() {
        var position = new Vector3(x: 0f, y: 5.3f, z: 0f);
        var red = Frame() with { Lights = Lamp(color: SdfPhotometry.UnitLuminance(color: Vector3.UnitX), position: position) };
        var green = red with { Lights = Lamp(color: SdfPhotometry.UnitLuminance(color: Vector3.UnitY), position: position) };
        var volume = Volume();

        _ = Trace(frame: red, tier: SdfIndirectTier.Medium, volume: volume);
        var (parameters, turned) = Trace(frame: green, tier: SdfIndirectTier.Medium, volume: volume);
        var (_, fresh) = Trace(frame: green, tier: SdfIndirectTier.Medium, volume: Volume());
        // The cell about (1, 5, 1) m, beside the lamp in the upper room.
        var after = Cube(probe: Probe(parameters: in parameters, words: turned, x: 0, y: 2, z: 0));
        var clean = Cube(probe: Probe(parameters: in parameters, words: fresh, x: 0, y: 2, z: 0));

        Assert.True(condition: (clean.Y > 0f) && (after.X <= ((0.02f * after.Y) + 1.0e-3f)), userMessage: $"turned green {after}, fresh green {clean}");
        Assert.True(condition: (MathF.Abs(x: (after.Y - clean.Y)) <= (0.05f * clean.Y)), userMessage: $"turned green {after}, fresh green {clean}");
    }

    /// <summary>A camera sample reused a few centimetres from where it was traced is relit from where it was traced, so
    /// after a light change it reads what a sample traced there reads.</summary>
    [Fact]
    public void AReusedCameraSampleIsRelitFromItsOwnOrigin() {
        var frame = Frame();
        var lights = new SdfLights();

        lights.Set(index: 0, light: frame.Lights.Records[0] with { Direction = new Vector3(x: 0f, y: 2.8f, z: 0f) });
        lights.Count = 1;
        frame = frame with {
            Lights = lights,
            IndirectBounces = 0,
            ConvergesLighting = true,
            MeshDraws = [.. frame.MeshDraws,
                Draw(mesh: Box(low: new Vector3(x: -10f, y: 0f, z: -10f), high: new Vector3(x: -0.09f, y: 4f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: 0.09f, y: 0f, z: -10f), high: new Vector3(x: 10f, y: 4f, z: 10f))),
            ],
        };

        var origin = frame.Views[0].Camera.Position;
        var moved = (origin + new Vector3(x: 0.05f, y: 0f, z: 0f));
        var volume = Volume();

        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => { });
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out _, position: origin, skyVisibility: out _, source: out var source));
        Assert.Equal(expected: SdfExposureMeterSource.CameraTrace, actual: source);
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out _, position: moved, skyVisibility: out _, source: out _));

        var traces = volume.Counters.Read(kind: SdfMeshIrradiance.CameraTraces);
        var dimmed = new SdfLights();

        dimmed.Set(index: 0, light: lights.Records[0] with { Color = new Vector3(x: 0.2f, y: 0.6f, z: 1f) });
        dimmed.Count = 1;
        frame = frame with { Lights = dimmed };
        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => { });
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var relit, position: moved, skyVisibility: out _, source: out _));
        Assert.Equal(expected: traces, actual: volume.Counters.Read(kind: SdfMeshIrradiance.CameraTraces));

        var fresh = Volume();

        _ = fresh.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => { });
        Assert.True(condition: fresh.TryMeter(forward: Vector3.UnitX, luminance: out var traced, position: origin, skyVisibility: out _, source: out _));
        Assert.Equal(expected: traced, actual: relit, tolerance: (1.0e-4f * traced));
    }

    // A uniform sky of one colour, in nits.
    private static SdfSky UniformSky(Vector3 color) {
        var sky = new SdfSky();
        var gradient = new SdfSkyGradient { Count = 2u };

        sky.ClearLayers();
        gradient.SetStop(color: color, elevation: -1f, index: 0);
        gradient.SetStop(color: color, elevation: 1f, index: 1);
        _ = sky.Add(label: "uniform", parameters: gradient, visibility: SdfSkyVisibility.Both);
        return sky;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraOnlyTwinkleDoesNotProjectOrAdvanceConvergedLighting(bool grid) {
        var sky = UniformSky(new Vector3(0.01f, 0.02f, 0.1f));
        _ = sky.Add(label: "camera stars", parameters: new SdfSkyStars { Brightness = 2f }, visibility: SdfSkyVisibility.Camera);
        var gains = new SdfIndirectGains(Lights: 0, Emission: 0, Screens: 0, Sky: 1, Feedback: 0);
        var frame = Frame() with {
            Sky = sky, Lights = new SdfLights(), IndirectGains = gains, IndirectSources = gains.Sources,
            ConvergesLighting = true, IndirectBounces = 0,
            IndirectLightingGrid = grid ? new SdfIndirectLightingGrid(1, 0.125f) : default,
        };
        var volume = Volume();
        // Exercise production capture invalidation independently of volume tracing.
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var layoutType = typeof(SdfMeshIrradiance).GetNestedType("Layout", BindingFlags.NonPublic)!;
        var layout = layoutType.GetMethod("Of", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [SdfIndirectTier.Medium, 0, frame.FarDistance]);
        var refresh = typeof(SdfMeshIrradiance).GetMethod("RefreshLighting", Private)!;
        var version = typeof(SdfMeshIrradiance).GetField("m_lightingVersion", Private)!;
        refresh.Invoke(volume, [frame, layout]);
        var initialVersion = version.GetValue(volume);
        for (var tick = 1; tick <= 8; tick++) {
            sky.First<SdfSkyStars>().TwinklePhase = tick * 0.1f;
            refresh.Invoke(volume, [frame, layout]);
        }
        Assert.Equal(1, volume.Counters.Read(SdfMeshIrradiance.EnvironmentProjections));
        Assert.Equal(initialVersion, version.GetValue(volume));
    }

    /// <summary>A dim blue added to a bright red sky relights the volume: the blue the slab sends back reaches the probes
    /// as it does in a fresh volume under the new sky, though it is a two-thousandth of the red.</summary>
    [Fact]
    public void ADimBlueAddedToABrightRedSkyStillRelights() {
        var gains = new SdfIndirectGains(Emission: 0f, Feedback: 1f, Lights: 0f, Screens: 0f, Sky: 1f);
        var red = Frame() with { IndirectGains = gains, IndirectSources = gains.Sources, Lights = new SdfLights(), Sky = UniformSky(color: new Vector3(x: 1000f, y: 0f, z: 0f)) };
        var blue = red with { Sky = UniformSky(color: new Vector3(x: 1000f, y: 0f, z: 0.5f)) };
        var volume = Volume();

        _ = Trace(frame: red, tier: SdfIndirectTier.Medium, volume: volume);
        var (parameters, changed) = Trace(frame: blue, tier: SdfIndirectTier.Medium, volume: volume);
        var (_, fresh) = Trace(frame: blue, tier: SdfIndirectTier.Medium, volume: Volume());
        var after = Cube(probe: Probe(parameters: in parameters, words: changed, x: 0, y: 2, z: 0));
        var clean = Cube(probe: Probe(parameters: in parameters, words: fresh, x: 0, y: 2, z: 0));

        Assert.True(condition: (clean.Z > 0f), userMessage: $"fresh {clean}");
        Assert.True(condition: (MathF.Abs(x: (after.Z - clean.Z)) <= (0.05f * clean.Z)), userMessage: $"relit {after}, fresh {clean}");
    }
}
