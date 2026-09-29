using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanSphereDistanceKeepsItsRoundedReductionAcrossVmPaths() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifySpherePrecision(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXSphereDistanceKeepsItsRoundedReductionAcrossVmPaths() {
        using var device = DirectXTestDevices.Hardware();

        VerifySpherePrecision(services: device.Services, extension: ".dxil");
    }

    private static void VerifySpherePrecision(GpuDeviceServices services, string extension) {
        // Exact beam samples at two neighboring silhouette tiles. Binary32 products, then the two ordered additions,
        // round their squared norms to 0x42787B65. The square root rounds to 0x40FC3687; subtracting radius 0x3F19999A
        // rounds to 0x40E90354. These words were derived from the dyadic inputs, not from either GPU's result.
        Vector3[] points = [
            new(x: BitConverter.UInt32BitsToSingle(value: 0xBC0258E7), y: BitConverter.UInt32BitsToSingle(value: 0x3FF33B08), z: BitConverter.UInt32BitsToSingle(value: 0x41404B27)),
            new(x: BitConverter.UInt32BitsToSingle(value: 0xBC0258D6), y: BitConverter.UInt32BitsToSingle(value: 0x3FF32B41), z: BitConverter.UInt32BitsToSingle(value: 0x41404B27)),
        ];
        var program = Pack(emit: static (builder, material) => builder.ResetPoint()
            .Translate(offset: new Vector3(x: 0f, y: 1.9f, z: 19.9f)).Sphere(radius: 0.6f, material: material));
        var kernel = File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: ("sdf-sphere-precision.comp" + extension)));
        var results = Run(kernel: kernel, points: points, services: services,
            legs: [new SdfFieldLeg(Name: "sphere reduction", Words: program.Words.ToArray(), Distances: [], Materials: [])]);

        foreach (var result in results) {
            Assert.Equal(expected: 0x40E90354u, actual: BitConverter.SingleToUInt32Bits(value: result.X));
            Assert.Equal(expected: 0x40E90354u, actual: BitConverter.SingleToUInt32Bits(value: result.Y));
            Assert.Equal(actual: result.Z, expected: 0f);
            Assert.Equal(actual: result.W, expected: 1f);
        }
    }
}

