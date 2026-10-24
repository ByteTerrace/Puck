using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: the irradiance volume keeps a static point light to the room it hangs in. Traced over two rooms stacked on a
/// slab, with one static point light in the upper room and nothing else, a probe in the upper room sees the light (its
/// visibility bit set) and holds the light the slab's top sends back, while a probe in the lower room sees neither; a
/// probe's depth cube reaches the slab along the axis toward it, so a read on the slab's far side takes nothing from it;
/// and the meter reads the upper room lit and the lower room dark.
/// </summary>
public sealed class SdfMeshIrradianceLawTests {
    // A box between two corners, its faces wound outward, as the bake winds them.
    private static SdfMesh Box(Vector3 low, Vector3 high) {
        Vector3[] positions = [
            new(low.X, low.Y, low.Z), new(high.X, low.Y, low.Z), new(high.X, high.Y, low.Z), new(low.X, high.Y, low.Z),
            new(low.X, low.Y, high.Z), new(high.X, low.Y, high.Z), new(high.X, high.Y, high.Z), new(low.X, high.Y, high.Z),
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
    private static (SdfMeshIrradianceParameters Parameters, uint[] Words) Trace(SdfMeshIrradiance volume, SdfFrame frame) {
        var words = Array.Empty<uint>();
        var parameters = default(SdfMeshIrradianceParameters);
        var deadline = DateTime.UtcNow.AddSeconds(value: 120d);

        do {
            parameters = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: memory => words = memory.ToArray());
            Thread.Sleep(millisecondsTimeout: 20);
        } while (volume.Settling && (DateTime.UtcNow < deadline));

        Assert.False(condition: volume.Settling);
        Assert.True(condition: (words.Length > 0), userMessage: volume.Describe());

        return (parameters, words);
    }
    private static ReadOnlySpan<uint> Probe(uint[] words, in SdfMeshIrradianceParameters parameters, int x, int y, int z) {
        var slot = SdfMeshIrradiance.SlotOf(dims: parameters.Dims, x: x, y: y, z: z);
        var probe = words.AsSpan(length: SdfMeshIrradianceProbe.ProbeWords, start: (slot * SdfMeshIrradianceProbe.ProbeWords));

        Assert.Equal(expected: (SdfMeshIrradiance.KeyOf(x: x, y: y, z: z) | SdfMeshIrradianceProbe.WrittenFlag), actual: probe[0]);

        return probe;
    }
    private static float Half(uint word, bool high) => ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(high ? (word >> 16) : (word & 0xffffu)))));
    private static float Luminance(ReadOnlySpan<uint> probe, int face) {
        var sum = 0f;

        for (var channel = 0; (channel < 3); channel++) {
            var entry = ((3 * face) + channel);

            sum += Half(high: ((entry & 1) != 0), word: probe[(4 + (entry >> 1))]);
        }

        return sum;
    }

    /// <summary>A lockstep frame waits for its scene and window's trace, including a cold scene and a replacement:
    /// each writes the same probes the asynchronous trace lands over many frames, in one staging.</summary>
    [Fact]
    public void ALockstepFrameRendersTheWindowTracedToTheEnd() {
        var (_, landed) = Trace(frame: Frame(), volume: new SdfMeshIrradiance());
        var locked = new SdfMeshIrradiance();
        var frame = (Frame() with { TickLocked = true });
        var writes = 0;
        var words = Array.Empty<uint>();
        _ = locked.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: memory => {
            writes++;
            words = memory.ToArray();
        });

        Assert.Equal(expected: 1, actual: writes);
        Assert.Equal(expected: landed, actual: words);

        _ = locked.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => writes++);
        Assert.Equal(expected: 1, actual: writes);

        var changed = frame with { MeshDraws = [frame.MeshDraws[0], frame.MeshDraws[2]] };
        var (_, replacement) = Trace(frame: changed with { TickLocked = false }, volume: new SdfMeshIrradiance());

        _ = locked.Stage(bakePending: false, frame: changed, tier: SdfIndirectTier.High, write: memory => {
            writes++;
            words = memory.ToArray();
        });
        Assert.Equal(expected: 2, actual: writes);
        Assert.Equal(expected: replacement, actual: words);
        Assert.NotEqual(expected: landed, actual: words);
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
    public void AStaticPointLightIsSeenAndBouncedInItsOwnRoomAlone_AndADepthCubeStopsAtTheSlab() {
        var volume = new SdfMeshIrradiance();
        var frame = Frame();
        var (parameters, words) = Trace(volume: volume, frame: frame);

        Assert.True(condition: parameters.Enabled);
        Assert.Equal(expected: 1f, actual: parameters.Spacing);

        // The probe at (0.5, 5.5, 0.5), beside the light in the upper room, and the one at (0.5, 1.5, 0.5) under the slab.
        var upper = Probe(parameters: in parameters, words: words, x: 0, y: 5, z: 0);
        var lower = Probe(parameters: in parameters, words: words, x: 0, y: 1, z: 0);

        Assert.Equal(expected: 1u, actual: (upper[SdfMeshIrradianceProbe.LightsWord] & 1u));
        Assert.Equal(expected: 0u, actual: (lower[SdfMeshIrradianceProbe.LightsWord] & 1u));
        // The slab's lit top sends light up to the upper probe's -Y face; the floor under the slab sends the lower
        // probe's only what spills round the slab's open edges and bounces back in, under a hundredth of it.
        var upperLuminance = Luminance(face: 3, probe: upper);

        Assert.True(condition: (upperLuminance > 0f));
        Assert.True(condition: (Luminance(face: 3, probe: lower) < (0.01f * upperLuminance)));
        // The lower probe's depth is the exact distance along each axis to the slab's underside 2.5 m up and the floor
        // 1.5 m down, and the upper probe's to the ceiling 2.8 m up, to the half precision it is stored at, so a point
        // on the slab's top, 0.3 m past the lower probe's depth, takes nothing from it.
        var lowerUp = Half(high: false, word: lower[(SdfMeshIrradianceProbe.DepthWord + 1)]);
        var lowerDown = Half(high: true, word: lower[(SdfMeshIrradianceProbe.DepthWord + 1)]);
        var upperUp = Half(high: false, word: upper[(SdfMeshIrradianceProbe.DepthWord + 1)]);

        Assert.InRange(actual: lowerUp, low: 2.49f, high: 2.51f);
        Assert.InRange(actual: lowerDown, low: 1.49f, high: 1.51f);
        Assert.InRange(actual: upperUp, low: 2.79f, high: 2.81f);
        // The meter reads the lit room lit and the dark room dark.
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var lit, position: new Vector3(x: 0.5f, y: 5.5f, z: 0.5f), skyVisibility: out _, source: out _));
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var dark, position: new Vector3(x: 0.5f, y: 1.5f, z: 0.5f), skyVisibility: out _, source: out _));
        Assert.True(condition: (lit > 0f));
        Assert.True(condition: (dark < (0.01f * lit)));

        // The camera can approach the slab without reading the lit probes beyond it.
        Assert.True(condition: volume.TryMeter(forward: Vector3.UnitX, luminance: out var nearSlab, position: new Vector3(x: 0.5f, y: 3.99f, z: 0.5f), skyVisibility: out _, source: out _));
        Assert.True(condition: (nearSlab < (0.01f * lit)));
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
            TickLocked = true,
            Exposure = new SdfExposure(Mode: SdfExposureMode.Auto, Ev100: 15f, Minimum: 0f, Maximum: 20f, Compensation: 0f, Adaptation: 0f, Local: 0.5f),
            // A 40-cm gap contains the camera and light but no lattice centres (x = +/- 0.5).
            MeshDraws = [.. frame.MeshDraws,
                Draw(mesh: Box(low: new Vector3(x: -10f, y: 0f, z: -10f), high: new Vector3(x: -0.2f, y: 4f, z: 10f))),
                Draw(mesh: Box(low: new Vector3(x: 0.2f, y: 0f, z: -10f), high: new Vector3(x: 10f, y: 4f, z: 10f))),
            ],
        };
        var volume = new SdfMeshIrradiance();

        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.High, write: _ => { });
        Assert.True(condition: volume.TryMeter(position: frame.Views[0].Camera.Position, forward: Vector3.UnitX,
            luminance: out var lit, skyVisibility: out var sky, source: out var source));
        Assert.Equal(expected: SdfExposureMeterSource.CameraTrace, actual: source);
        Assert.True(condition: (lit > 0f));
        Assert.InRange(actual: sky, low: 0f, high: 0.1f);
        var exposures = new SdfViewExposures();

        exposures.Stage(frame: frame, irradiance: new SdfMeshIrradiance(), seconds: 1d);
        Assert.Equal(expected: SdfPhotometry.ExposureOf(ev100: frame.Exposure.Ev100), actual: exposures.MultiplierOf(view: 0));
        Assert.Contains(expectedSubstring: "meter=none source=initial", actualString: exposures.Describe()[0]);
        exposures.Stage(frame: frame, irradiance: volume, seconds: 1d);
        Assert.Equal(expected: SdfPhotometry.ExposureOf(ev100: frame.Exposure.Target(luminance: lit)), actual: exposures.MultiplierOf(view: 0), precision: 6);
        Assert.Contains(expectedSubstring: "source=camera-trace", actualString: exposures.Describe()[0]);
        var held = exposures.MultiplierOf(view: 0);
        var heldAir = exposures.AirLightOf(view: 0);

        exposures.Stage(frame: frame, irradiance: new SdfMeshIrradiance(), seconds: 1d);
        Assert.Equal(expected: held, actual: exposures.MultiplierOf(view: 0));
        Assert.Equal(expected: heldAir, actual: exposures.AirLightOf(view: 0));
        Assert.Equal(expected: lit, actual: exposures.MeteredOf(view: 0));
        Assert.Contains(expectedSubstring: "meter=none source=held", actualString: exposures.Describe()[0]);
        // Inside the opaque wall is unknown, not a reading of an unlit room.
        Assert.False(condition: volume.TryMeter(position: new Vector3(x: 0.3f, y: 2f, z: 0f), forward: Vector3.UnitX,
            luminance: out _, skyVisibility: out _, source: out source));
        Assert.Equal(expected: SdfExposureMeterSource.None, actual: source);
    }

    [Fact]
    public void AnOpeningBesideAnAxisDoesNotEraseItsFirstBlocker() {
        var frame = Frame() with {
            IndirectBounces = 0,
            MeshDraws = [Draw(mesh: Box(low: new Vector3(x: 1f, y: 0.4f, z: 0.4f), high: new Vector3(x: 1.1f, y: 0.6f, z: 0.6f)))],
        };
        var (parameters, words) = Trace(volume: new SdfMeshIrradiance(), frame: frame);
        var probe = Probe(parameters: in parameters, words: words, x: 0, y: 0, z: 0);

        Assert.InRange(actual: Half(high: false, word: probe[SdfMeshIrradianceProbe.DepthWord]), low: 0.499f, high: 0.501f);
    }
}
