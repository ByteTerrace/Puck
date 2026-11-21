using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    // Every shape and blend with a shared formula (SdfFieldFormulas) lowers one expression to both readers: the kernel and
    // the fixed-point evaluator agree within each carrier's rounding of it, far from the shape as well as near it, on
    // both arms of every branch (both lifts, the round cone's three regions, the vesica's tip, each blend's seam). The
    // superellipsoid runs at small radii, eccentric radii and every clamped exponent; its gauge scales by its smallest
    // radius, so its agreement is held relative to that scale as well as absolutely.
    [Fact]
    public void AVulkanDeviceReadsTheShapeAndBlendFormulasAsTheEvaluatorDoes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyShapeFormulas(backend: $"vulkan ({device.Name})", extension: ".spv", services: device.Services);
    }
    [Fact]
    public void ADirect3D12DeviceReadsTheShapeAndBlendFormulasAsTheEvaluatorDoes() {
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
        ("box, rounded", static (b, m) => b.ResetPoint().Box(halfExtents: new Vector3(x: 0.5f, y: 0.25f, z: 1f), material: m, round: 0.1f),
            [new(x: 0.1f, y: 0.1f, z: 0.1f), new(x: 0.6f, y: 0.3f, z: 1.2f), new(x: 3f, y: 0f, z: 0f), new(x: 0f, y: -0.2f, z: 0.9f)]),
        ("sphere", static (b, m) => b.ResetPoint().Sphere(material: m, radius: 0.75f),
            [new(x: 0f, y: 0f, z: 0f), new(x: 0.5f, y: 0.5f, z: 0.5f), new(x: 20f, y: -3f, z: 1f)]),
        ("torus", static (b, m) => b.ResetPoint().Torus(majorRadius: 1f, material: m, minorRadius: 0.25f),
            [new(x: 1f, y: 0f, z: 0f), new(x: 0f, y: 0.5f, z: 0f), new(x: 0.3f, y: 0.1f, z: 1.4f)]),
        ("cylinder, rounded", static (b, m) => b.ResetPoint().Cylinder(halfHeight: 0.6f, material: m, radius: 0.4f, rounding: 0.05f),
            [new(x: 0f, y: 0f, z: 0f), new(x: 0.5f, y: 0.7f, z: 0f), new(x: 0.1f, y: 2f, z: 0.1f), new(x: 0.3f, y: 0.1f, z: 0.2f)]),
        ("plane", static (b, m) => b.ResetPoint().Plane(material: m, normal: Vector3.Normalize(value: new Vector3(x: 0.2f, y: 1f, z: 0.1f)), offset: 0.3f),
            [new(x: 0f, y: 0f, z: 0f), new(x: 4f, y: -2f, z: 1f)]),
        ("vesica", static (b, m) => b.ResetPoint().Vesica(halfSeparation: 0.3f, material: m, radius: 0.5f),
            [new(x: 0f, y: 0.5f, z: 0f), new(x: 0.1f, y: 0.1f, z: 0f), new(x: 0.4f, y: 0f, z: 0.3f), new(x: 0f, y: -2f, z: 0f)]),
        ("round cone", static (b, m) => b.ResetPoint().RoundCone(height: 1f, lowerRadius: 0.4f, material: m, upperRadius: 0.15f),
            [new(x: 0f, y: -1f, z: 0f), new(x: 0.5f, y: 0.5f, z: 0f), new(x: 0f, y: 1.5f, z: 0.1f), new(x: 0.1f, y: 0.3f, z: 0.1f)]),
        ("rounded rectangle, extruded and capped", static (b, m) => b.ResetPoint().RoundedRectangle(capChamfer: 0.03f, cornerRadius: 0.1f, halfHeight: 0.3f, halfWidth: 0.5f, lift: SdfLift.Extrude, liftAmount: 0.2f, material: m, rounding: 0.02f),
            [new(x: 0f, y: 0f, z: 0f), new(x: 0.6f, y: 0.35f, z: 0.25f), new(x: 0f, y: 0f, z: 1f)]),
        ("rounded rectangle, revolved", static (b, m) => b.ResetPoint().RoundedRectangle(cornerRadius: 0.05f, halfHeight: 0.3f, halfWidth: 0.2f, lift: SdfLift.Revolve, liftAmount: 0.6f, material: m),
            [new(x: 0.6f, y: 0f, z: 0f), new(x: 0f, y: 0f, z: 0f), new(x: 0.5f, y: 0.5f, z: 0.5f)]),
        ("chamfered rectangle, extruded", static (b, m) => b.ResetPoint().ChamferedRectangle(chamfer: 0.08f, halfHeight: 0.4f, halfWidth: 0.3f, lift: SdfLift.Extrude, liftAmount: 0.25f, material: m),
            [new(x: 0.3f, y: 0.4f, z: 0f), new(x: 0f, y: 0f, z: 0.3f), new(x: 0.5f, y: 0.5f, z: 0.5f)]),
        ("chamfered rectangle, revolved", static (b, m) => b.ResetPoint().ChamferedRectangle(chamfer: 0.05f, halfHeight: 0.4f, halfWidth: 0.2f, lift: SdfLift.Revolve, liftAmount: 0.5f, material: m),
            [new(x: 0.7f, y: 0.4f, z: 0f), new(x: 0f, y: 0f, z: 0f)]),
        ("trapezoid, extruded and capped", static (b, m) => b.ResetPoint().Trapezoid(bottomHalfWidth: 0.6f, capChamfer: 0.02f, halfHeight: 0.4f, lift: SdfLift.Extrude, liftAmount: 0.2f, material: m, topHalfWidth: 0.2f),
            [new(x: 0f, y: 0f, z: 0f), new(x: 0.5f, y: 0.3f, z: 0f), new(x: -0.7f, y: -0.5f, z: 0.3f), new(x: 0f, y: 1f, z: 0f)]),
        ("trapezoid, revolved", static (b, m) => b.ResetPoint().Trapezoid(bottomHalfWidth: 0.3f, halfHeight: 0.5f, lift: SdfLift.Revolve, liftAmount: 0f, material: m, topHalfWidth: 0.1f),
            [new(x: 0.1f, y: 0f, z: 0.1f), new(x: 0.4f, y: -0.6f, z: 0f)]),
        ("ellipse, extruded", static (b, m) => b.ResetPoint().Ellipse(lift: SdfLift.Extrude, liftAmount: 0.1f, material: m, semiX: 0.6f, semiY: 0.3f),
            [new(x: 0f, y: 0f, z: 0f), new(x: 0.7f, y: 0.2f, z: 0.05f), new(x: 0f, y: 0f, z: 0.5f)]),
        ("ellipse, revolved", static (b, m) => b.ResetPoint().Ellipse(lift: SdfLift.Revolve, liftAmount: 0f, material: m, semiX: 0.3f, semiY: 0.7f),
            [new(x: 0.1f, y: 0.2f, z: 0f), new(x: 1f, y: 1f, z: 1f)]),
        .. BlendCases(),
    ];

    // Each blend composes a box into a sphere it overlaps, at a radius wide enough to reach every seam. A scope-only
    // blend composes a field scope, which this leg does not open; its arm reads the union or subtraction formula.
    private static IEnumerable<(string, Func<SdfProgramBuilder, int, SdfProgramBuilder>, Vector3[])> BlendCases() =>
        Enum.GetValues<SdfBlendOp>().Where(predicate: static op => op is not (SdfBlendOp.Morph or SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction)).Select(selector: static op => (
            $"blend {op}",
            (Func<SdfProgramBuilder, int, SdfProgramBuilder>)((b, m) => b.ResetPoint().Sphere(material: m, radius: 0.6f)
                .ResetPoint().Translate(offset: new Vector3(x: 0.5f, y: 0f, z: 0f)).Box(blend: op, halfExtents: new Vector3(x: 0.4f, y: 0.4f, z: 0.4f), material: m, round: 0f, smooth: 0.25f)),
            new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 0.35f, y: 0.45f, z: 0f), new(x: 0.55f, y: 0f, z: 0f), new(x: 1.2f, y: 0.1f, z: 0f), new(x: -0.8f, y: 0.2f, z: 0.1f) }));

    private static void VerifyShapeFormulas(string backend, string extension, GpuDeviceServices services) {
        var failures = new List<string>();

        foreach (var (name, emit, points) in ShapeFormulaCases) {
            var program = Pack(emit: emit);
            var evaluator = new SdfFieldEvaluator(program: program);
            var references = points.Select(selector: point => Reference(evaluator: evaluator, point: point).Distance).ToArray();
            var results = Run(kernel: Kernel(extension: extension), legs: [new SdfFieldLeg(Distances: references, Materials: [], Name: name, Words: program.CopyWords())], points: points, services: services);

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
