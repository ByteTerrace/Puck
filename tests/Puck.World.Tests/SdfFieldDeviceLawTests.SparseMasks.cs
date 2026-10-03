using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanSparseTileTapesPreserveMaskedInstancesAndWorldSegments() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifySparseTileTapes(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXSparseTileTapesPreserveMaskedInstancesAndWorldSegments() {
        using var device = DirectXTestDevices.Hardware();

        VerifySparseTileTapes(services: device.Services, extension: ".dxil");
    }

    private static void VerifySparseTileTapes(GpuDeviceServices services, string extension) {
        const int InstanceCount = 66;
        var builder = new SdfProgramBuilder();
        var worldMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));
        var visibleMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
        var hiddenMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitZ));

        builder.ResetPoint().Sphere(radius: 1, material: worldMaterial);
        for (var instance = 0; (instance < InstanceCount); instance++) {
            var visible = ((instance == 1) || (instance == 65));
            var radius = (visible ? ((instance == 1) ? 200f : 80f) : ((instance == 0) ? 2000f : 1000f));

            builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: radius);
            if (visible) {
                for (var shape = 1; (shape <= 40); shape++) {
                    builder.ResetPoint().Sphere(radius: ((radius / 40) * shape), material: visibleMaterial);
                }
            } else {
                builder.ResetPoint().Sphere(radius: radius, material: hiddenMaterial);
            }
            builder.EndInstance();
            if (instance == 32) {
                builder.ResetPoint().Sphere(radius: 2, material: worldMaterial);
            }
        }
        builder.ResetPoint().Sphere(radius: 100, material: worldMaterial);
        var program = builder.Build(buildInstanceGrid: false);

        Assert.Equal(expected: InstanceCount, actual: program.Instances.Count);

        var maskWords = SdfProgram.InstanceMaskWordCountFor(instanceCount: InstanceCount);
        var maskStride = SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: InstanceCount);
        var masks = new uint[(2 * maskStride)];

        masks[0] = (1u << 1);
        masks[maskWords] = 1u;
        masks[(maskStride + 2)] = (1u << 1);
        masks[(maskStride + maskWords)] = (1u << 2);
        // Equal rays with different mask bases distinguish the visible first-word
        // winner from the third-word instance that loses to the final world segment.
        Vector3[] points = [Vector3.Zero, Vector3.Zero];
        Vector4[] expected = [
            new(w: hiddenMaterial, x: 1, y: visibleMaterial, z: 0),
            new(w: hiddenMaterial, x: -1, y: worldMaterial, z: 0),
        ];
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-tape-sparse-masks.comp"), services: services,
            legs: [GradientLeg(name: "sparse camera masks", program: program)], points: points, parameters: expected,
            transforms: GradientTransforms, instanceMasks: masks,
            tapeWordsPerCase: SdfWorldPackage.SegmentTapeWordCountFor(segments: program.SkipSegmentCount, tokens: program.TapeTokenCount));

        AssertTapeResults(results: results);
        for (var tile = 0; (tile < results.Length); tile++) {
            Assert.True(condition: (results[tile].X > 0),
                userMessage: $"tile {tile}: its masked tape must save counted shape evaluations itself");
            Assert.True(condition: (results[tile].Z > 0),
                userMessage: $"tile {tile}: its masked samples must read their own tape");
            Assert.True(condition: (results[tile].W > 0),
                userMessage: $"tile {tile}: its masked tape must count the shapes it evaluates");
        }
    }
}
