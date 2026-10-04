using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The indirect gather keeps every surface in its reach, and the march relinquishes that mask at its
/// boundary. The probe compares the production masked march to the full field and samples every cleared interval.
/// Cone, ball and box walks also produce exactly the flat query's bits with one and sixty-four lanes.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed partial class SdfIndirectGatherLawTests {
    private const string Kernel = "sdf-indirect-gather.comp";
    private const int ResultRows = 5;

    private sealed record GatherCase(string Name, SdfProgram Program, Vector3 Origin, Vector3 Direction,
        float Reach, float Far, float? ExpectedHit = null);

    [Fact]
    public void VulkanGathersAndClipsAgainstTheFullField() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectGatherLawTests));

        Verify(extension: ".spv", services: device.Services);
    }
    [Fact]
    public void DirectXGathersAndClipsAgainstTheFullField() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) {
            Verify(extension: ".dxil", services: device.Services);
        }
        Assert.DoesNotContain(comparisonType: StringComparison.Ordinal, expectedSubstring: "[d3d12-debug]", actualString: output.ToString());
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        var cases = Cases();
        var rows = new Vector4[(cases.Length * 2)];

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            rows[(index * 2)] = new Vector4(value: item.Origin, w: item.Reach);
            rows[((index * 2) + 1)] = new Vector4(value: item.Direction, w: item.Far);
        }
        var results = SdfIndirectDeviceProbe.Run(programs: cases.Select(selector: static item => item.Program).ToArray(), rows: rows,
            extension: extension, kernel: Kernel, resultRows: ResultRows, services: services, transforms: Transforms());

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];
            var masked = results[index];
            var full = results[(cases.Length + index)];
            var intervals = results[((2 * cases.Length) + index)];
            var queries = results[((3 * cases.Length) + index)];
            var controls = results[((4 * cases.Length) + index)];

            Assert.True(condition: (masked.X == full.X), userMessage: $"{item.Name}: masked kind {masked.X}, full kind {full.X}");
            Assert.True(condition: (MathF.Abs(x: (masked.Y - full.Y)) <= 0.002f),
                userMessage: $"{item.Name}: masked distance {masked.Y}, full distance {full.Y}");
            if (item.ExpectedHit is { } expected) {
                Assert.Equal(actual: full.X, expected: ((float)IrradianceHitKind.Hit));
                Assert.InRange(actual: full.Y, high: (expected + 0.002f), low: (expected - 0.002f));
            }
            Assert.True(condition: (intervals.X == 0f), userMessage: $"{item.Name}: {intervals.X} full-field occupied samples in a cleared interval");
            Assert.True(condition: (intervals.Y == 0f), userMessage: $"{item.Name}: {intervals.Y} masked steps passed the reach");
            Assert.True(condition: (intervals.Z > 0f), userMessage: $"{item.Name}: no cleared interval was checked");
            Assert.True(condition: (queries == Vector4.Zero), userMessage: $"{item.Name}: cone/ball/box/strided mask differences {queries}");
            Assert.Equal(actual: controls.X, expected: 1f);
            if (index < 4) {
                Assert.True(condition: (controls.W == 1f), userMessage: $"{item.Name}: the fixture did not exercise the masked march");
            }
            if (item.Name == "the outer spacing remains gathered") {
                Assert.True(condition: (controls.Y > 0f), userMessage: "The short-ball control did not drop an instance inside the reach.");
            }
            if (item.Name == "an occluder after the reach remains visible") {
                Assert.True(condition: (controls.Z > 0f), userMessage: "The retained-mask control did not exclude the later occluder.");
            }
        }
    }
    private static GatherCase[] Cases() => [
        new("the outer spacing remains gathered", Sphere(radius: 0.2f, slot: 0), Vector3.Zero, Vector3.UnitX, 4, 10, 3.4f),
        new("an instance just outside the reach remains visible", Sphere(radius: 0.2f, slot: 1), Vector3.Zero, Vector3.UnitX, 4, 10, 4.05f),
        new("an occluder after the reach remains visible", Sphere(radius: 0.2f, slot: 2), Vector3.Zero, Vector3.UnitX, 4, 10, 6.8f),
        new("a clear ray exhausts the full interval", Sphere(radius: 0.2f, slot: 3), Vector3.Zero, Vector3.UnitX, 4, 10),
        new("a subtractive composition keeps its operand", Carved(), Vector3.Zero, Vector3.UnitX, 4, 10, 3.25f),
        new("a smooth composition keeps its halo", Smooth(), Vector3.Zero, Vector3.UnitX, 4, 10),
        new("a wallpaper fold preserves cleared intervals", Wallpaper(), new Vector3(x: 6.9f, y: 0, z: 0), -Vector3.UnitX, 1, 5),
        new("a log-sphere fold preserves cleared intervals", LogSphere(), new Vector3(x: 22.7f, y: 0, z: 0), -Vector3.UnitX, 0.04f, 1),
    ];
    private static SdfProgramBuilder Builder() {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        for (var index = 0; (index < 32); index++) {
            var center = new Vector3(x: (-24 + ((index % 8) * 3)), y: (10 + ((index / 8) * 3)), z: 0);

            _ = builder.BeginInstance(boundCenter: center, boundRadius: 0.2f)
                .ResetPoint().Translate(offset: center).Sphere(material: 0, radius: 0.2f).EndInstance();
        }
        return builder;
    }

    // The sphere cases' centers, one dynamic slot each, so each sphere compiles to a part program the masked march can
    // trace independently; a statically translated instance compiles no part and keeps the full field.
    private static readonly Vector3[] SphereCenters = [new(x: 3.6f, y: 0, z: 0), new(x: 4.25f, y: 0, z: 0), new(x: 7, y: 0, z: 0), new(x: 3, y: 2, z: 0)];

    private static Vector4[] Transforms() => [.. SphereCenters.SelectMany(selector: static center => new[] {
        new Vector4(value: center, w: 0), new Vector4(w: 1, x: 0, y: 0, z: 0), Vector4.Zero,
    })];
    private static SdfProgram Sphere(int slot, float radius) {
        var builder = Builder();

        _ = builder.BeginInstanceDynamic(boundOffset: Vector3.Zero, boundRadius: radius, slot: slot)
            .PushField().ResetPoint().TransformDynamic(slot: slot).Sphere(material: 0, radius: radius).PopField().EndInstance();
        return builder.Build();
    }
    private static SdfProgram Carved() {
        var builder = Builder();

        _ = builder.BeginInstance(boundCenter: new Vector3(x: 3, y: 0, z: 0), boundRadius: 1)
            .ResetPoint().Translate(offset: new Vector3(x: 3, y: 0, z: 0)).Sphere(material: 0, radius: 1)
            .EndInstance().BeginInstance(boundCenter: new Vector3(x: 2.5f, y: 0, z: 0), boundRadius: 0.75f)
            .ResetPoint().Translate(offset: new Vector3(x: 2.5f, y: 0, z: 0)).Sphere(blend: SdfBlendOp.Subtraction, material: 0, radius: 0.75f)
            .EndInstance();
        return builder.Build();
    }
    private static SdfProgram Smooth() {
        var builder = Builder();

        _ = builder.BeginInstance(boundCenter: new Vector3(x: 3, y: 0.3f, z: 0), boundRadius: 0.5f)
            .ResetPoint().Translate(offset: new Vector3(x: 3, y: 0.3f, z: 0)).Sphere(material: 0, radius: 0.5f)
            .EndInstance().BeginInstance(boundCenter: new Vector3(x: 3, y: -0.3f, z: 0), boundRadius: 0.5f)
            .ResetPoint().Translate(offset: new Vector3(x: 3, y: -0.3f, z: 0)).Sphere(blend: SdfBlendOp.SmoothUnion, material: 0, radius: 0.5f, smooth: 0.4f)
            .EndInstance();
        return builder.Build();
    }
    private static SdfProgram Wallpaper() {
        var builder = Builder();

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 20)
            .ResetPoint().WallpaperFold(cell: new Vector2(value: 2), group: SdfWallpaperGroup.Pmm, limit: new Vector2(value: 8))
            .Translate(offset: new Vector3(x: 0.4f, y: 0, z: 0)).Sphere(material: 0, radius: 0.2f).EndInstance();
        return builder.Build();
    }
    private static SdfProgram LogSphere() {
        var builder = Builder();

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 32)
            .ResetPoint().LogSphere(shellRatio: 2)
            .Translate(offset: new Vector3(x: 1.41406f, y: 0, z: 0)).Sphere(material: 0, radius: 0.0003125f).EndInstance();
        return builder.Build();
    }
}
