using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanPowerGaugeEnforcesItsCertifiedNormEnvelope() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyGaugeEnvelope(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXPowerGaugeEnforcesItsCertifiedNormEnvelope() {
        using var device = DirectXTestDevices.Hardware();

        VerifyGaugeEnvelope(services: device.Services, extension: ".dxil");
    }

    private static void VerifyGaugeEnvelope(GpuDeviceServices services, string extension) {
        var points = new[] {
            new Vector3(x: -100f, y: 8f, z: 2.5f), new Vector3(x: 100f, y: 8f, z: 2.5f), new Vector3(x: 7.5f, y: 8f, z: 2.5f),
            new Vector3(x: float.NaN, y: 8f, z: 2.5f), new Vector3(x: float.NegativeInfinity, y: 8f, z: 2.5f),
            new Vector3(x: float.PositiveInfinity, y: 8f, z: 2.5f), new Vector3(x: -100f, y: 8f, z: 3f), new Vector3(x: 100f, y: 0f, z: 2.5f),
        };
        var expected = new[] { 6.88f, 8f, 7.5f, 8f, 8f, 8f, 5.76f, 0f };
        var program = Pack(emit: static (builder, material) => builder.ResetPoint().Sphere(radius: 1f, material: material));
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-gauge-envelope.comp"), services: services,
            legs: [new SdfFieldLeg(Name: "power norm envelope", Words: program.Words.ToArray(), Distances: [], Materials: [])], points: points);

        Assert.Equal(expected: expected.Length, actual: results.Length);
        for (var index = 0; (index < results.Length); index++) {
            Assert.InRange(actual: results[index].X, low: (expected[index] - 0.000002f), high: (expected[index] + 0.000002f));
            Assert.Equal(expected: ((index == 2) ? 0f : 1f), actual: results[index].Y);
        }
    }
}
