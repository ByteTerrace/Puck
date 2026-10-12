using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    [Fact]
    public void VulkanNexusTileTapesPreserveTheFullMarchSamples() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyNexusTapes(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXNexusTileTapesPreserveTheFullMarchSamples() {
        using var device = DirectXTestDevices.Hardware();

        VerifyNexusTapes(services: device.Services, extension: ".dxil");
    }
    private static void VerifyNexusTapes(GpuDeviceServices services, string extension) {
        var (program, transforms, directions, parameters) = NexusFieldProbe();
        var results = Run(kernel: GradientKernel(extension: extension, name: "sdf-tape-rays.comp"), services: services,
            tapeBuildKernel: GradientKernel(extension: extension, name: "sdf-tape-rays-build.comp"),
            legs: [GradientLeg(name: "Nexus", program: program)], points: directions, parameters: parameters,
            transforms: transforms,
            tapeWordsPerCase: SdfWorldPackage.SegmentTapeWordCountFor(segments: program.SkipSegmentCount, tokens: program.TapeTokenCount));

        AssertTapeResults(results: results);
    }
}
