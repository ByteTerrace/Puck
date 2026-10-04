using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanGradientsEvaluateExactlyTheFinalWeightedShapes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyGradients(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXGradientsEvaluateExactlyTheFinalWeightedShapes() {
        using var device = DirectXTestDevices.Hardware();

        VerifyGradients(services: device.Services, extension: ".dxil");
    }
    [Fact]
    public void VulkanNexusHitGradientsEvaluateExactlyTheFinalWeightedShapes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyNexusGradients(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXNexusHitGradientsEvaluateExactlyTheFinalWeightedShapes() {
        using var device = DirectXTestDevices.Hardware();

        VerifyNexusGradients(services: device.Services, extension: ".dxil");
    }

    private static readonly Vector4[] GradientTransforms = [
        new(w: 0, x: 0.125f, y: -0.25f, z: 0.375f),
        new(w: 1, x: 0, y: 0, z: 0),
        new(w: 0, x: 0.375f, y: 0, z: 0),
        new(w: 0, x: 20, y: 0, z: 0),
        new(w: 1, x: 0, y: 0, z: 0),
        new(w: 0, x: 0, y: 0, z: 0),
    ];

    private static byte[] GradientKernel(string name, string extension) => File.ReadAllBytes(path: Path.Combine(
        path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (name + extension)));
    private static void VerifyGradients(GpuDeviceServices services, string extension) {
        var legs = GradientLegs().ToArray();
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-gradient.comp"), services: services,
            legs: legs, transforms: GradientTransforms);

        AssertGradientResults(results: results, names: legs.Select(selector: static leg => leg.Name).ToArray(), pointsPerLeg: Points.Length);
        Assert.Contains(collection: results, filter: static result => (result.Z > 1));
        for (var leg = 0; (leg < legs.Length); leg++) {
            if (!legs[leg].Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "overflow discarded")) {
                continue;
            }
            Assert.All(collection: results.AsSpan(start: (leg * Points.Length), length: Points.Length).ToArray(), action: static result => {
                Assert.Equal(actual: result.Y, expected: 1f);
                Assert.Equal(actual: result.Z, expected: 1f);
                Assert.True(condition: (result.X > 0f));
            });
        }
        VerifyGradientCounts(extension: extension, services: services);
    }
    private static void AssertGradientResults(Vector4[] results, string[] names, int pointsPerLeg) {
        var hits = 0;
        var saved = 0f;

        for (var index = 0; (index < results.Length); index++) {
            var result = results[index];

            if (result.Z < 0) {
                continue;
            }
            hits++;
            saved += result.X;
            var context = $"{names[(index / pointsPerLeg)]}, sample {(index % pointsPerLeg)}: {result}";

            Assert.True(condition: (result.Y == result.Z), userMessage: $"analytic evaluations must equal analytically supported nonzero weights in the full basis walk: {context}");
            Assert.True(condition: (result.W <= 0.003f), userMessage: $"gradient or bit-exact field value differs: {context}");
        }

        Assert.True(condition: (hits >= 8), userMessage: $"only {hits} hit samples executed");
        Assert.True(condition: (saved > 0), userMessage: $"winner finding plus selected gradients saved {saved} shape evaluations");
    }
    private static void VerifyGradientCounts(GpuDeviceServices services, string extension) {
        foreach (var compiled in new[] { false, true }) {
            var legs = new[] {
                GradientLeg(name: "analytic sphere", program: GradientCounterProgram(compiled: compiled, finiteDifference: false)),
                GradientLeg(name: "finite difference round cone", program: GradientCounterProgram(compiled: compiled, finiteDifference: true)),
            };
            var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-gradient-counts.comp"), services: services,
                legs: legs, transforms: GradientTransforms);
            var winnerSearch = (compiled ? 9 : 1);
            var fullAnalytic = (compiled ? 18 : 2);

            Assert.All(collection: results.AsSpan(start: 0, length: Points.Length).ToArray(), action: result =>
                Assert.Equal(expected: new Vector4(w: fullAnalytic, x: (winnerSearch + 1), y: 1, z: 1), actual: result));
            Assert.All(collection: results.AsSpan(start: Points.Length, length: Points.Length).ToArray(), action: result =>
                Assert.Equal(expected: new Vector4(w: (fullAnalytic + 3), x: (winnerSearch + 4), y: 0, z: 0), actual: result));
        }
    }
    private static SdfProgram GradientCounterProgram(bool compiled, bool finiteDifference) => Pack(emit: (builder, material) => {
        if (compiled) {
            builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 12);
            builder.PushField();
            for (var index = 0; (index < 8); index++) {
                builder.ResetPoint().Sphere(radius: (0.1f * (index + 1)), material: material);
            }
        }
        builder.ResetPoint();
        if (finiteDifference) {
            builder.RoundCone(lowerRadius: 4, upperRadius: 4, height: 4, material: material);
        } else {
            builder.Sphere(radius: 4, material: material);
        }
        if (compiled) {
            builder.PopField();
            builder.EndInstance();
        }
        return builder;
    });
    private static IEnumerable<SdfFieldLeg> GradientLegs() {
        yield return GradientLeg(name: "nested parent and child contributors", program: Pack(emit: static (builder, material) => builder
            .Sphere(.8f, material).PushField(compose: SdfBlendOp.SmoothUnion, smooth: .4f)
            .ResetPoint().Translate(Vector3.UnitX * .25f).Sphere(.75f, material)
            .PushField(compose: SdfBlendOp.SmoothUnion, smooth: .3f)
            .ResetPoint().Translate(Vector3.UnitY * .25f).Sphere(.7f, material)
            .ResetPoint().Sphere(.2f, material, blend: SdfBlendOp.Subtraction).PopField().PopField()));
        foreach (var blend in Enum.GetValues<SdfBlendOp>()) {
            if (blend is SdfBlendOp.Morph or SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction) {
                continue;
            }

            var program = Pack(emit: (builder, material) => {
                for (var index = 0; (index < 8); index++) {
                    builder.ResetPoint().Translate(offset: new Vector3(x: (index * 0.03f), y: 0, z: 0))
                        .Sphere(radius: (0.3f + (index * 0.12f)), material: material);
                }
                return builder.ResetPoint().Translate(offset: new Vector3(x: 0.2f, y: 0.15f, z: -0.1f))
                    .Sphere(radius: 1.05f, material: material, blend: blend, smooth: 0.35f);
            });

            yield return GradientLeg(name: blend.ToString(), program: program);
        }

        yield return GradientLeg(name: "scoped warped relief", program: Pack(emit: static (builder, material) => builder
            .ResetPoint().Sphere(radius: 0.6f, material: material)
            .PushField(compose: SdfBlendOp.SmoothUnion, smooth: 0.45f)
            .ResetPoint().Scale(scale: new Vector3(x: 1.2f, y: 0.9f, z: 1.1f))
            .Shear(linear: 0.1f, quadratic: 0.07f)
            .Sphere(radius: 0.9f, material: material)
            .Displace(frequency: new Vector3(x: 0.7f, y: 0.5f, z: 0.3f), amplitude: 0.02f)
            .Onion(thickness: 0.2f).PopField()));
        yield return GradientLeg(name: "dynamic morph", program: Pack(emit: static (builder, material) => builder
            .ResetPoint().Sphere(radius: 0.8f, material: material)
            .PushFieldMorph(from: 0, laneIndex: 0, to: 1)
            .ResetPoint().TransformDynamic(slot: 0).Sphere(radius: 0.75f, material: material).PopField()));
        foreach (var subtract in new[] { false, true }) {
            yield return GradientLeg(name: $"stairs subtract={subtract}", program: Pack(emit: (builder, material) => builder
                .ResetPoint().Sphere(radius: 0.8f, material: material)
                .PushFieldStairs(radius: 0.3f, steps: 3, subtraction: subtract)
                .ResetPoint().Translate(offset: new Vector3(x: 0.5f, y: 0.1f, z: 0))
                .Sphere(radius: 0.7f, material: material).PopField()));
        }
        yield return GradientLeg(name: "contributor overflow keeps the full dual", program: Pack(emit: static (builder, material) => {
            for (var index = 0; (index < 40); index++) {
                builder.ResetPoint().Sphere(radius: 1, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f);
            }
            return builder;
        }));
        foreach (var scope in new[] { 0, 1, 2, 3 }) {
            yield return GradientLeg(name: $"overflow discarded scope={scope}", program: Pack(emit: (builder, material) => {
                if (scope >= 2) {
                    builder.ResetPoint().Sphere(radius: ((scope == 2) ? 4f : 0.5f), material: material).PushField();
                }
                for (var index = 0; (index < 40); index++) {
                    builder.ResetPoint().Sphere(radius: 1, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f);
                }
                if (scope == 1) {
                    builder.PushField();
                }
                if (scope != 2) {
                    builder.ResetPoint().Sphere(radius: 4f, material: material);
                }
                return ((scope == 0) ? builder : builder.PopField());
            }));
        }
    }
    private static SdfFieldLeg GradientLeg(string name, SdfProgram program) =>
        new(Name: name, Words: program.Words.ToArray(), Distances: [], Materials: []);
    private static void VerifyNexusGradients(GpuDeviceServices services, string extension) {
        var (program, transforms, directions, parameters) = NexusFieldProbe();
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-gradient-rays.comp"), services: services,
            legs: [GradientLeg(name: "Nexus", program: program)], transforms: transforms,
            points: directions, parameters: parameters);

        AssertGradientResults(results: results, names: ["Nexus"], pointsPerLeg: directions.Length);
    }
    private static (SdfProgram Program, Vector4[] Transforms, Vector3[] Directions, Vector4[] Parameters) NexusFieldProbe() {
        const string RelativePath = "tests/Puck.Counters/nexus.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: RelativePath);
        var frame = ComposedSdfWorldFixture.Capture(definition: definition, relativePath: RelativePath);
        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: 0.9f, position: new Vector3(x: 0, y: 2.5f, z: 7),
            target: new Vector3(x: 0, y: 0.8f, z: 0), viewportWidth: 1440, viewportHeight: 810);
        var directions = new List<Vector3>();
        // One pixel near each regular lattice cell's centre, at the floor render extent.
        for (var y = 40; (y < 810); y += 80) {
            for (var x = 40; (x < 1440); x += 80) {
                var ndcX = ((((x + 0.5f) / 1440f) * 2) - 1);
                var ndcY = (1 - (((y + 0.5f) / 810f) * 2));

                directions.Add(item: Vector3.Normalize(value: ((camera.Forward
                    + (((ndcX * camera.AspectRatio) * camera.TanHalfFieldOfView) * camera.Right))
                    + ((ndcY * camera.TanHalfFieldOfView) * camera.Up))));
            }
        }
        return (frame.Program, ComposedSdfWorldFixture.PackTransforms(frame: frame), directions.ToArray(), [new Vector4(value: camera.Position, w: SdfFrameBlock.NearOf(camera: camera)),
            new Vector4(value: camera.Forward, w: WorldRenderFarDistance.Resolve(defaults: definition.Render)),
            new Vector4(x: (((MathF.Sqrt(x: 2) * 16) * camera.TanHalfFieldOfView) / 810), y: 0, z: 0, w: 0)]);
    }
}
