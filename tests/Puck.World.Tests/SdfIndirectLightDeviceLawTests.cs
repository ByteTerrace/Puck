using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The GPU conservative sweep agrees with known CPU visibility and independently bounds every answer.
/// The same subtexel rod must disappear when the sweep alone becomes a point ray; a thin nearby caster checks the
/// finite camera's production near-plane helper while an ordinary-camera control retains its perspective floor.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectLightDeviceLawTests {
    [Fact]
    public void VulkanSeesSubtexelCastersThroughTheConservativeLightView() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectLightDeviceLawTests));
        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXSeesSubtexelCastersThroughTheConservativeLightView() {
        using var output = new StringWriter();
        using (var device = DirectXTestDevices.Debug(output: output)) {
            Verify(services: device.Services, extension: ".dxil");
        }
        Assert.DoesNotContain(comparisonType: StringComparison.Ordinal, expectedSubstring: "[d3d12-debug]", actualString: output.ToString());
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        VerifyNearPlane(services: services, extension: extension);
        const int Resolution = 64;
        var program = IrradianceLightFixture.Program();
        var field = new IrradianceField(program: program);
        var projection = IrradianceLightFixture.Projection(resolution: Resolution);
        var cpu = new IrradianceLightView(field: field, projection: projection);
        var receivers = IrradianceLightFixture.Receivers();
        Vector4[] rows = [
            Row(projection.Origin, projection.SweepRadius), Row(projection.Right, projection.HalfWidth),
            Row(projection.Up, projection.Near), Row(projection.TowardLight, projection.Far),
            new Vector4(x: Resolution, y: receivers.Length, z: 0, w: 0),
            .. receivers.Select(selector: point => Row(point, 0)),
        ];
        var results = SdfIndirectDeviceProbe.Run(services: services, extension: extension, kernel: "sdf-indirect-light-proof.comp",
            resultRows: (receivers.Length + 1), programs: [program, program], rows: rows, cacheWords: (Resolution * Resolution), passValues: PassValues(lightCamera: true));
        var answered = 0;
        var cpuAnswered = 0;
        var missed = 0;
        var widened = 0;
        for (var index = 0; (index < receivers.Length); index++) {
            var point = receivers[index];
            var expected = cpu.Lit(point: point, normal: IrradianceLightFixture.Up);
            var swept = results[(index * 2)];
            var pointRay = results[((index * 2) + 1)];
            var inside = projection.Project(point: point, column: out var column, row: out var row, travel: out var travel);
            var cpuDepth = (inside ? cpu.Depths[((row * Resolution) + column)] : double.NaN);
            var evidence = $"{extension}: receiver {index} at {point}, CPU visibility {expected?.ToString() ?? "unknown"}, " +
                $"texel ({column}, {row}), depth {cpuDepth}, travel {travel}; GPU swept {swept}, point ray {pointRay}.";
            Assert.True(condition: (swept.X is -1f or 0f or 1f), userMessage: evidence);
            // An unfinished fixed-point certificate is unknown, not a visibility verdict. The floating-point
            // sweep can finish that certificate; every answer still faces the independent segment and width bounds.
            if (expected.HasValue) {
                Assert.True(condition: (swept.X == (expected.Value ? 1f : 0f)), userMessage: evidence);
                cpuAnswered++;
            }
            if (swept.X >= 0f) { answered++; }
            var exact = field.SegmentClear(from: (point + (IrradianceLightFixture.Up * 0.004)), to: (point + (IrradianceLightFixture.Sun * 10)));
            Assert.False(condition: (swept.X == 1f && !exact), userMessage: $"Lit through a caster: {evidence}");
            if (exact && (swept.X == 0f)) {
                Assert.True(condition: IrradianceLightFixture.NearShadow(point: point, radius: (2 * cpu.TexelSize), sun: IrradianceLightFixture.Sun),
                    userMessage: $"Shadowed beyond two texels: {evidence}");
                widened++;
            }
            if (!exact && (pointRay.X == 1f)) { missed++; }
        }
        Assert.True(condition: (answered > 0));
        Assert.True(condition: (cpuAnswered > 0));
        Assert.True(condition: (widened > 0));
        Assert.True(condition: (missed > 0), userMessage: $"{extension}: the zero-radius discriminator did not miss the rod.");
    }
    private static void VerifyNearPlane(GpuDeviceServices services, string extension) {
        const int Resolution = 64;
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        builder.Translate(offset: new Vector3(x: 0, y: 0.005f, z: 0));
        builder.Box(halfExtents: new Vector3(x: 0.01f, y: 0.001f, z: 0.01f), material: material, round: 0);
        var projection = IrradianceLightProjection.Create(
            receiverMin: new Double3(X: -0.01, Y: 0, Z: -0.01), receiverMax: new Double3(X: 0.01, Y: 0, Z: 0.01),
            casterMin: new Double3(X: -0.01, Y: 0.004, Z: -0.01), casterMax: new Double3(X: 0.01, Y: 0.006, Z: 0.01),
            towardLight: IrradianceLightFixture.Up, penumbraSlope: 0.1, resolution: Resolution);
        Vector4[] rows = [Row(projection.Origin, projection.SweepRadius), Row(projection.Right, projection.HalfWidth),
            Row(projection.Up, projection.Near), Row(projection.TowardLight, projection.Far),
            new Vector4(x: Resolution, y: 1, z: 0, w: 0), Vector4.Zero];
        var program = builder.Build();
        Vector4[] Run(bool lightCamera) => SdfIndirectDeviceProbe.Run(services: services, extension: extension,
            kernel: "sdf-indirect-light-proof.comp", resultRows: 2, programs: [program], rows: rows,
            cacheWords: (Resolution * Resolution), passValues: PassValues(lightCamera: lightCamera));
        var light = Run(lightCamera: true);
        // The box lies directly between the origin receiver and the overhead sun, farther than the comparison bias.
        // Clamping this finite camera to the perspective minimum starts past the complete box and reports it lit.
        Assert.Equal(expected: 0f, actual: light[0].X);
        Assert.Equal(expected: ((float)projection.Near), actual: light[1].X);
        var ordinary = Run(lightCamera: false);
        Assert.Equal(expected: SdfWorldPackage.MinimumNear, actual: ordinary[1].X);
    }
    private static byte[] PassValues(bool lightCamera) {
        var parameters = SdfWorldInterfaces.IndirectParameters;
        var values = new byte[parameters.SizeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(destination: values.AsSpan(start: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.LightMap))),
            value: (lightCamera ? 1u : 0u));
        return values;
    }
    private static Vector4 Row(Double3 point, double value) => new(x: ((float)point.X), y: ((float)point.Y), z: ((float)point.Z), w: ((float)value));
}
