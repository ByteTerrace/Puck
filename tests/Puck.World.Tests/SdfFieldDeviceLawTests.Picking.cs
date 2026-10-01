using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanPickingKeepsTheWinningShapeSlotSeparateFromTheInstanceBound() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyPicking(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXPickingKeepsTheWinningShapeSlotSeparateFromTheInstanceBound() {
        using var device = DirectXTestDevices.Hardware();

        VerifyPicking(services: device.Services, extension: ".dxil");
    }

    private static void VerifyPicking(GpuDeviceServices services, string extension) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.BeginInstance(boundCenter: new Vector3(value: 1000), boundRadius: 2);
        builder.ResetPoint().Translate(offset: new Vector3(value: 1000)).Sphere(radius: 1, material: material);
        builder.EndInstance();
        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 200);
        builder.ResetPoint().TransformDynamic(slot: 1).Sphere(radius: 5, material: material);
        builder.ResetPoint().TransformDynamic(slot: 2).Sphere(radius: 1, material: material);
        builder.EndInstance();
        var program = builder.Build(buildInstanceGrid: false);

        Assert.Equal(expected: 0, actual: program.Instances[1].Slot);
        Vector4[] transforms = [
            new(w: 0, x: 0, y: 0, z: 0), new(w: 1, x: 0, y: 0, z: 0), new(w: 40, x: 10, y: 20, z: 30),
            new(w: 0, x: 0, y: 0, z: 0), new(w: 1, x: 0, y: 0, z: 0), new(w: 0.75f, x: 0.125f, y: 0.25f, z: 0.5f),
            new(w: 0, x: 100, y: 0, z: 0), new(w: 1, x: 0, y: 0, z: 0), new(w: 4, x: 1, y: 2, z: 3),
        ];
        var kernel = File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: ("sdf-pick.comp" + extension)));
        var results = Run(kernel: kernel, services: services,
            legs: [new SdfFieldLeg(Name: "articulated", Words: program.Words.ToArray(), Distances: [], Materials: [])], transforms: transforms);

        foreach (var result in results) {
            Assert.Equal(expected: new Vector4(w: 1, x: 2, y: 1, z: 1), actual: result);
        }
    }
}
