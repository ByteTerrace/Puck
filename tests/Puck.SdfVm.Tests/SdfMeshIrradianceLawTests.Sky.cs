using System.Numerics;
using System.Reflection;
using Puck.Abstractions.Cameras;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky light's law: a probe's open rays carry the sky's radiance along each of them, so a receiver reads the
/// sky its probes see, not a share of the whole hemisphere's irradiance.</summary>
public sealed partial class SdfMeshIrradianceLawTests {
    // A day sky: grey below the horizon, a pale horizon, a deep blue zenith, in nits.
    private static SdfSky DaySky() {
        var sky = new SdfSky();
        var gradient = new SdfSkyGradient { Count = 4u };

        sky.ClearLayers();
        gradient.SetStop(color: new Vector3(x: 1100f, y: 1400f, z: 1700f), elevation: -1f, index: 0);
        gradient.SetStop(color: new Vector3(x: 6200f, y: 7200f, z: 7900f), elevation: 0f, index: 1);
        gradient.SetStop(color: new Vector3(x: 2100f, y: 4200f, z: 6600f), elevation: 0.4f, index: 2);
        gradient.SetStop(color: new Vector3(x: 280f, y: 1070f, z: 2600f), elevation: 1f, index: 3);
        _ = sky.Add(label: "day", parameters: gradient, visibility: SdfSkyVisibility.Both);
        return sky;
    }

    /// <summary>THE LAW: a wall standing on open ground under a day sky reads the sky its probes see. With only the sky
    /// lighting the volume, the converged indirect light at the wall's face matches, in every colour channel within 3%,
    /// the sky's radiance integrated over the directions from the face that miss the ground and the wall, cosine weighted.
    /// A sky share times the hemisphere's irradiance reads too much red and too little blue there, since the half of the
    /// sky the ground hides is the grey below the horizon, not a share of the whole.</summary>
    [Fact]
    public void AnOpenWallReadsTheSkyItsRaysSee() {
        var gains = new SdfIndirectGains(Emission: 0f, Feedback: 0f, Lights: 0f, Screens: 0f, Sky: 1f);
        var sky = DaySky();
        SdfMeshDraw[] draws = [
            Draw(mesh: Box(low: new Vector3(x: -300f, y: -0.3f, z: -300f), high: new Vector3(x: 300f, y: 0f, z: 300f))),
            Draw(mesh: Box(low: new Vector3(x: -2f, y: 0f, z: -0.1f), high: new Vector3(x: 2f, y: 3f, z: 0.1f))),
        ];
        var frame = Frame() with {
            ConvergesLighting = true,
            FarDistance = 400f,
            IndirectBounces = 0,
            IndirectGains = gains,
            IndirectSources = gains.Sources,
            IndirectTier = SdfIndirectTier.Medium,
            Lights = new SdfLights(),
            MeshDraws = draws,
            Sky = sky,
            Views = [Frame().Views[0] with { Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: 0f, y: 1.6f, z: 3f), target: new Vector3(x: 0f, y: 1.6f, z: 0f), viewportHeight: 4u, viewportWidth: 4u) }],
        };
        var volume = Volume();

        _ = volume.Stage(bakePending: false, frame: frame, tier: SdfIndirectTier.Medium, write: _ => { });

        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var point = new Vector3(x: 0f, y: 1.6f, z: 0.1f);
        var normal = Vector3.UnitZ;
        var layout = typeof(SdfMeshIrradiance).GetField(name: "m_layout", bindingAttr: Private)!.GetValue(obj: volume);
        var lows = typeof(SdfMeshIrradiance).GetMethod(name: "Lows", bindingAttr: Private)!.Invoke(obj: volume, parameters: null);
        var read = (Vector3)typeof(SdfMeshIrradiance).GetMethod(name: "Incoming", bindingAttr: Private)!.Invoke(obj: volume, parameters: [point, normal, layout, lows])!;

        using var scene = SdfMeshIrradianceScene.Build(draws: draws);
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(lights: new SdfLights(), farDistance: frame.FarDistance, details: new SdfSkyDetails(), block: out var block, layers: layers);

        // The cosine-weighted mean of the sky's radiance over the open directions: a stratified grid mapped onto the
        // hemisphere about the normal by the disc's area (each cell an equal share of the cosine-weighted hemisphere).
        const int Cells = 160;
        var tangent = Vector3.UnitX;
        var bitangent = Vector3.Cross(vector1: normal, vector2: tangent);
        var expected = Vector3.Zero;

        for (var row = 0; (row < Cells); row++) {
            for (var column = 0; (column < Cells); column++) {
                var u = ((column + 0.5f) / Cells);
                var v = ((row + 0.5f) / Cells);
                var radius = MathF.Sqrt(x: u);
                var angle = ((2f * MathF.PI) * v);
                var direction = Vector3.Normalize(value: (((tangent * (radius * MathF.Cos(x: angle))) + (bitangent * (radius * MathF.Sin(x: angle)))) + (normal * MathF.Sqrt(x: (1f - u)))));

                if (scene.Intersect(direction: direction, distance: out _, material: out _, maximum: 1000f, normal: out _, origin: (point + (normal * 1e-3f)))) { continue; }
                expected += SdfSkyEnvironment.Evaluate(block: in block, direction: direction, layers: layers);
            }
        }
        expected /= (Cells * Cells);

        for (var channel = 0; (channel < 3); channel++) {
            Assert.True(condition: (MathF.Abs(x: (read[channel] - expected[channel])) <= (0.03f * expected[channel])), userMessage: $"volume {read}, sky integral {expected}");
        }
    }
}
