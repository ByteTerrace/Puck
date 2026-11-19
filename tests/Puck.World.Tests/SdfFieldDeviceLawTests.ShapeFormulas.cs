using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    // The superellipsoid and the capsule read one formula in both readers: the kernel and the fixed-point evaluator agree
    // within each carrier's rounding of the same expression at small radii, eccentric radii, every clamped exponent, and
    // long, short and degenerate capsules, far from the shape as well as near it. The superellipsoid's gauge scales by
    // its smallest radius, so its agreement is held relative to that scale as well as absolutely.
    [Fact]
    public void AVulkanDeviceReadsTheSuperellipsoidAndTheCapsuleAsTheEvaluatorDoes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyShapeFormulas(backend: $"vulkan ({device.Name})", extension: ".spv", services: device.Services);
    }
    [Fact]
    public void ADirect3D12DeviceReadsTheSuperellipsoidAndTheCapsuleAsTheEvaluatorDoes() {
        using var context = DirectXTestDevices.Hardware();

        VerifyShapeFormulas(backend: "directx", extension: ".dxil", services: context.Services);
    }

    private static readonly (string Name, Func<SdfProgramBuilder, int, SdfProgramBuilder> Emit, Vector3[] Points)[] ShapeFormulaCases = [
        ("superellipsoid, the smallest admitted radius", static (b, m) => b.ResetPoint().Superellipsoid(exponent: 2f, material: m, radii: new Vector3(x: 0.0001f, y: 0.01f, z: 0.01f)),
            [new(x: 0f, y: 100f, z: 0f), new(x: 0.5f, y: 0.25f, z: 0f), new(x: 0.001f, y: 0.003f, z: 0.002f), new(x: 0f, y: 0f, z: 7f)]),
        ("superellipsoid, eccentric radii", static (b, m) => b.ResetPoint().Superellipsoid(exponent: 2f, material: m, radii: new Vector3(x: 0.001f, y: 100f, z: 100f)),
            [new(x: 0f, y: 0.5f, z: 0f), new(x: 0.2f, y: 50f, z: 3f), new(x: 1f, y: 120f, z: 0f)]),
        ("superellipsoid, a clamped exponent", static (b, m) => b.ResetPoint().Superellipsoid(exponent: 2.6f, material: m, radii: new Vector3(x: 0.3f, y: 0.5f, z: 0.7f)),
            [new(x: 0.25f, y: 0.25f, z: 0.25f), new(x: 3f, y: 2f, z: 1f), new(x: 0.01f, y: 0.6f, z: 0.01f), new(x: -0.4f, y: 0.1f, z: 0.9f)]),
        ("superellipsoid, exponent three", static (b, m) => b.ResetPoint().Superellipsoid(exponent: 3f, material: m, radii: new Vector3(x: 0.3f, y: 0.5f, z: 0.7f)),
            [new(x: 0.25f, y: 0.25f, z: 0.25f), new(x: 3f, y: 2f, z: 1f), new(x: -0.4f, y: 0.1f, z: 0.9f)]),
        ("superellipsoid, exponent eight", static (b, m) => b.ResetPoint().Superellipsoid(exponent: 8f, material: m, radii: new Vector3(x: 0.3f, y: 0.5f, z: 0.7f)),
            [new(x: 0.25f, y: 0.25f, z: 0.25f), new(x: 30f, y: 20f, z: 10f), new(x: -0.4f, y: 0.1f, z: 0.9f)]),
        ("capsule, long", static (b, m) => b.ResetPoint().Capsule(endpoint: new Vector3(x: 0f, y: 40f, z: 3f), material: m, radius: 0.5f),
            [new(x: 1f, y: 20f, z: 1f), new(x: 0f, y: -3f, z: 0f), new(x: 2f, y: 45f, z: 4f), new(x: 0.3f, y: 10f, z: 0.75f)]),
        ("capsule, short", static (b, m) => b.ResetPoint().Capsule(endpoint: new Vector3(x: 0.01f, y: 0.02f, z: 0f), material: m, radius: 0.005f),
            [new(x: 0.005f, y: 0.01f, z: 0.004f), new(x: 1f, y: 1f, z: 1f), new(x: -0.02f, y: 0f, z: 0f)]),
        ("capsule, a sphere", static (b, m) => b.ResetPoint().Capsule(endpoint: Vector3.Zero, material: m, radius: 0.5f),
            [new(x: 0f, y: 0f, z: 0f), new(x: 0.3f, y: 0.4f, z: 0f), new(x: 2f, y: -1f, z: 0.5f)]),
    ];

    private static void VerifyShapeFormulas(string backend, string extension, GpuDeviceServices services) {
        var failures = new List<string>();

        foreach (var (name, emit, points) in ShapeFormulaCases) {
            var program = Pack(emit: emit);
            var evaluator = new SdfFieldEvaluator(program: program);
            var references = points.Select(selector: point => Reference(evaluator: evaluator, point: point).Distance).ToArray();
            var results = Run(kernel: Kernel(extension: extension), legs: [new SdfFieldLeg(Distances: references, Materials: [], Name: name, Words: program.Words.ToArray())], points: points, services: services);

            for (var index = 0; (index < points.Length); index++) {
                var device = (results[index].X / results[index].Z);
                var miss = MathF.Abs(x: (device - references[index]));

                // Each carrier rounds the same expression: Q48.16 to 2⁻¹⁶ per step, float to a few ulps of the value.
                if (!(miss <= (Tolerance + (1e-5f * MathF.Abs(x: references[index]))))) {
                    failures.Add(item: $"{name} at {points[index]}: the device answers {device}, the evaluator {references[index]}");
                }
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: $"{backend}:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: failures)}");
    }
}
