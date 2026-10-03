using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The production view domain preserves probe diagnostics in empty sky and clips them against scene hits.</summary>
[Collection(DebugLayerCollection.Name)]
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfIndirectDebugDeviceLawTests {
    [Fact]
    public void VulkanPreservesProbeExtentAndSceneOcclusion() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfIndirectDebugDeviceLawTests));

        Verify(extension: ".spv", services: device.Services);
    }
    [Fact]
    public void DirectXPreservesProbeExtentAndSceneOcclusion() {
        using var output = new StringWriter();

        using (var device = DirectXTestDevices.Debug(output: output)) {
            Verify(extension: ".dxil", services: device.Services);
        }
        Assert.DoesNotContain(comparisonType: StringComparison.Ordinal, expectedSubstring: "[d3d12-debug]", actualString: output.ToString());
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        Assert.True(DebugViewModes.TryParse(name: "indirect-probes", mode: out var probes));
        Assert.True(DebugViewModes.TryParse(name: "indirect-cells", mode: out var cells));
        Vector4 sky = new(-1, -1, 0, 0);
        Vector4 scene = new(2, 3, 5, 7);
        Vector4 viewport = new(0, 0, 15, 11);
        (Vector4 Surviving, int Mode, int Meshes, bool Hit, float Depth, Vector4 ExpectedBox, float ExpectedMaximum)[] cases = [
            (sky, probes, 0, false, 0, viewport, 80),
            (scene, probes, 0, false, 2, viewport, 80),
            (scene, probes, 0, true, 4, viewport, 4),
            (sky, 0, 0, false, 0, Vector4.Zero, 80),
            (scene, 0, 0, true, 9, scene, 9),
            (sky, cells, 0, false, 0, Vector4.Zero, 80),
            (scene, cells, 0, true, 9, scene, 9),
            (sky, 0, 1, false, 0, viewport, 80),
        ];
        var rows = new Vector4[(cases.Length * 3)];

        for (var index = 0; (index < cases.Length); index++) {
            var item = cases[index];

            rows[(index * 3)] = item.Surviving;
            rows[((index * 3) + 1)] = new Vector4(16, 12, item.Meshes, item.Mode);
            rows[((index * 3) + 2)] = new Vector4((item.Hit ? 1 : 0), item.Depth, 80, 0);
        }
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Sphere(material: 0, radius: 1).Build();
        var results = SdfIndirectDeviceProbe.Run(extension: extension, kernel: "sdf-indirect-debug-proof.comp",
            programs: Enumerable.Repeat(program, cases.Length).ToArray(), resultRows: 2, rows: rows, services: services);

        for (var index = 0; (index < cases.Length); index++) {
            Assert.Equal(expected: cases[index].ExpectedBox, actual: results[index]);
            Assert.Equal(expected: new Vector4(cases[index].ExpectedMaximum, 0, 0, 0), actual: results[(cases.Length + index)]);
        }
    }
}
