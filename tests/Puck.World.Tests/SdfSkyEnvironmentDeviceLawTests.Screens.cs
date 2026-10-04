using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfSkyEnvironmentDeviceLawTests {
    [Fact]
    public void VulkanReducesAcquiredScreenPixelsAndExcludesNonEmittingOrMissingSources() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfSkyEnvironmentDeviceLawTests));
        VerifyScreens(device.Services, ".spv");
    }
    [Fact]
    public void DirectXReducesAcquiredScreenPixelsAndExcludesNonEmittingOrMissingSources() {
        using var device = DirectXTestDevices.Hardware();
        VerifyScreens(device.Services, ".dxil");
    }

    private static void VerifyScreens(GpuDeviceServices services, string extension) {
        // A nine-by-seven image: its last column is red, every pixel green, and its first row blue. Unequal cell
        // populations distinguish the true whole-image mean from the unweighted average of sixteen cell means.
        var pixels = new byte[9 * 7 * 4];
        for (var y = 0; y < 7; y++) {
            for (var x = 0; x < 9; x++) {
                var at = (y * 9 + x) * 4;
                pixels[at] = x == 8 ? (byte)255 : (byte)0;
                pixels[at + 1] = 255;
                pixels[at + 2] = y == 0 ? (byte)255 : (byte)0;
                pixels[at + 3] = 255;
            }
        }
        var sky = new SdfSky();
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];
        sky.Pack(SdfLights.Default(), 40f, new SdfSkyDetails(), out var block, layers);
        var first = Run(services, extension, block, layers, pixels, screenWidth: 9, screenHeight: 7);
        var repeat = Run(services, extension, block, layers, pixels, screenWidth: 9, screenHeight: 7);
        Assert.Equal(first.Screens, repeat.Screens);
        Assert.Equal(63L, first.ScreenLoads);
        Assert.Equal(32L * 17L, first.ScreenWrites);
        for (var y = 0; y < 4; y++) {
            for (var x = 0; x < 4; x++) {
                Near(new Vector4(x == 3 ? 1f / 3f : 0f, 1f, y == 0 ? 1f : 0f, 1f), Read(first.Screens, y * 4 + x));
            }
        }
        Near(new Vector4(1f / 9f, 1f, 1f / 7f, 2.5f), Read(first.Screens, 16));
        Assert.All(first.Screens.Skip(17 * 16), value => Assert.Equal((byte)0, value));

        // A one-pixel fill must illuminate the whole face. Empty subdivision cells replicate that pixel while the
        // whole-image mean still counts it once; this also observes a real changed acquired texture, not a host color.
        var fill = Run(services, extension, block, layers, new byte[] { 255, 0, 255, 255 });
        for (var cell = 0; cell < 16; cell++) { Near(new Vector4(1f, 0f, 1f, 1f), Read(fill.Screens, cell)); }
        Near(new Vector4(1f, 0f, 1f, 2.5f), Read(fill.Screens, 16));
        Assert.Equal(16L, fill.ScreenLoads);
        Assert.Equal(32L * 17L, fill.ScreenWrites);

        static Vector4 Read(byte[] bytes, int row) => new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(row * 16)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(row * 16 + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(row * 16 + 8)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(row * 16 + 12)));
        static void Near(Vector4 expected, Vector4 actual) => Assert.True(Vector4.Distance(expected, actual) <= 1e-6f,
            $"Expected the acquired-image reduction {expected}, got {actual}.");
    }
}
