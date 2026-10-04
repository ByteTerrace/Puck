using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The GPU conservative sweep and projected visibility agree with the existing CPU field reference.
/// The same subtexel rod must disappear when the sweep alone becomes a point ray.</summary>
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
            resultRows: receivers.Length, programs: [program, program], rows: rows, cacheWords: (Resolution * Resolution));
        var answered = 0;
        var missed = 0;
        var widened = 0;
        for (var index = 0; (index < receivers.Length); index++) {
            var point = receivers[index];
            var expected = cpu.Lit(point: point, normal: IrradianceLightFixture.Up);
            var swept = results[(index * 2)];
            var pointRay = results[((index * 2) + 1)];
            Assert.Equal(expected: (expected.HasValue ? (expected.Value ? 1f : 0f) : -1f), actual: swept.X);
            if (expected.HasValue) { answered++; }
            var exact = field.SegmentClear(from: (point + (IrradianceLightFixture.Up * 0.004)), to: (point + (IrradianceLightFixture.Sun * 10)));
            Assert.False(condition: (swept.X == 1f && !exact), userMessage: $"{extension}: lit through a caster at {point}, result {swept}.");
            if (exact && (swept.X == 0f)) {
                Assert.True(condition: IrradianceLightFixture.NearShadow(point: point, radius: (2 * cpu.TexelSize), sun: IrradianceLightFixture.Sun),
                    userMessage: $"{extension}: shadowed beyond two texels at {point}, result {swept}.");
                widened++;
            }
            if (!exact && (pointRay.X == 1f)) { missed++; }
        }
        Assert.True(condition: (answered > 0));
        Assert.True(condition: (widened > 0));
        Assert.True(condition: (missed > 0), userMessage: $"{extension}: the zero-radius discriminator did not miss the rod.");
    }
    private static Vector4 Row(Double3 point, double value) => new(x: ((float)point.X), y: ((float)point.Y), z: ((float)point.Z), w: ((float)value));
}
