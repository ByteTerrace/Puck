using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanNexusHitGradientsEvaluateExactlyTheFinalWeightedShapes() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyNexusGradients(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXNexusHitGradientsEvaluateExactlyTheFinalWeightedShapes() {
        using var device = DirectXTestDevices.Hardware();

        VerifyNexusGradients(services: device.Services, extension: ".dxil");
    }
    private static void VerifyNexusGradients(GpuDeviceServices services, string extension) {
        var (program, transforms, directions, parameters) = NexusFieldProbe();
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-gradient-rays.comp"), services: services,
            legs: [GradientLeg(name: "Nexus", program: program)], transforms: transforms,
            points: directions, parameters: parameters);

        AssertGradientResults(results: results, names: ["Nexus"], pointsPerLeg: directions.Length);
    }
    private static (SdfProgram Program, Vector4[] Transforms, Vector3[] Directions, Vector4[] Parameters) NexusFieldProbe() {
        const string RelativePath = "tests/Puck.Counters/nexus.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: RelativePath);
        var frame = ComposedSdfWorldFixture.Capture(definition: definition, relativePath: RelativePath);
        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: 0.9f, position: new Vector3(x: 0, y: 2.5f, z: 7),
            target: new Vector3(x: 0, y: 0.8f, z: 0), viewportWidth: 1440, viewportHeight: 810);
        var directions = new List<Vector3>();
        // One pixel near each regular lattice cell's centre, at the floor render extent.
        for (var y = 40; (y < 810); y += 80) {
            for (var x = 40; (x < 1440); x += 80) {
                var ndcX = ((((x + 0.5f) / 1440f) * 2) - 1);
                var ndcY = (1 - (((y + 0.5f) / 810f) * 2));

                directions.Add(item: Vector3.Normalize(value: ((camera.Forward
                    + (((ndcX * camera.AspectRatio) * camera.TanHalfFieldOfView) * camera.Right))
                    + ((ndcY * camera.TanHalfFieldOfView) * camera.Up))));
            }
        }
        return (frame.Program, ComposedSdfWorldFixture.PackTransforms(frame: frame), directions.ToArray(), [new Vector4(value: camera.Position, w: SdfFrameBlock.NearOf(camera: camera)),
            new Vector4(value: camera.Forward, w: WorldRenderFarDistance.Resolve(defaults: definition.Render)),
            new Vector4(x: (((MathF.Sqrt(x: 2) * 16) * camera.TanHalfFieldOfView) / 810), y: 0, z: 0, w: 0)]);
    }
}
