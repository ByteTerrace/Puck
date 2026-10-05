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
        var pixels = new byte[((9 * 7) * 4)];

        for (var y = 0; (y < 7); y++) {
            for (var x = 0; (x < 9); x++) {
                var at = (((y * 9) + x) * 4);

                pixels[at] = ((x == 8) ? (byte)255 : (byte)0);
                pixels[(at + 1)] = 255;
                pixels[(at + 2)] = ((y == 0) ? (byte)255 : (byte)0);
                pixels[(at + 3)] = 255;
            }
        }
        var sky = new SdfSky();
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(SdfLights.Default(), 40f, new SdfSkyDetails(), out var block, layers);
        var first = Run(services, extension, block, layers, pixels, screenWidth: 9, screenHeight: 7);
        var repeat = Run(services, extension, block, layers, pixels, screenWidth: 9, screenHeight: 7);

        Assert.Equal(actual: repeat.Screens, expected: first.Screens);
        Assert.Equal(actual: first.ScreenLoads, expected: 63L);
        Assert.Equal(actual: first.ScreenWrites, expected: (32L * 17L));
        for (var y = 0; (y < 4); y++) {
            for (var x = 0; (x < 4); x++) {
                Near(new Vector4(w: 1f, x: ((x == 3) ? (1f / 3f) : 0f), y: 1f, z: ((y == 0) ? 1f : 0f)), Read(bytes: first.Screens, row: ((y * 4) + x)));
            }
        }
        Near(new Vector4(w: 2.5f, x: (1f / 9f), y: 1f, z: (1f / 7f)), Read(bytes: first.Screens, row: 16));
        Assert.All(first.Screens.Skip(count: (17 * 16)), value => Assert.Equal(actual: value, expected: ((byte)0)));

        // A one-pixel fill must illuminate the whole face. Empty subdivision cells replicate that pixel while the
        // whole-image mean still counts it once; this also observes a real changed acquired texture, not a host color.
        var fill = Run(services, extension, block, layers, new byte[] { 255, 0, 255, 255 });

        for (var cell = 0; (cell < 16); cell++) { Near(new Vector4(w: 1f, x: 1f, y: 0f, z: 1f), Read(bytes: fill.Screens, row: cell)); }
        Near(new Vector4(w: 2.5f, x: 1f, y: 0f, z: 1f), Read(bytes: fill.Screens, row: 16));
        Assert.Equal(actual: fill.ScreenLoads, expected: 16L);
        Assert.Equal(actual: fill.ScreenWrites, expected: (32L * 17L));

        // A later finite round rewrites only its derived screen. The independent records remain byte-identical,
        // and a genuinely dark acquired image still writes a valid terminal rather than retaining its old glow.
        var seed = new byte[SdfScreenEmission.Bytes];

        for (var word = 0; (word < (seed.Length / sizeof(float))); word++) {
            BinaryPrimitives.WriteSingleLittleEndian(destination: seed.AsSpan(start: (word * sizeof(float))), value: (word + 1f));
        }
        var dark = Run(services, extension, block, layers, new byte[] { 0, 0, 0, 255 }, screenWriteMask: 1u, screenSeed: seed);

        Assert.Equal(seed.AsSpan(start: (17 * 16)).ToArray(), dark.Screens.AsSpan(start: (17 * 16)).ToArray());
        for (var cell = 0; (cell < 16); cell++) { Near(new Vector4(w: 1f, x: 0f, y: 0f, z: 0f), Read(bytes: dark.Screens, row: cell)); }
        Near(new Vector4(w: 2.5f, x: 0f, y: 0f, z: 0f), Read(bytes: dark.Screens, row: 16));
        Assert.Equal(actual: dark.ScreenLoads, expected: 16L);
        Assert.Equal(actual: dark.ScreenWrites, expected: 17L);

        static Vector4 Read(byte[] bytes, int row) => new(
            BinaryPrimitives.ReadSingleLittleEndian(source: bytes.AsSpan(start: (row * 16))),
            BinaryPrimitives.ReadSingleLittleEndian(source: bytes.AsSpan(start: ((row * 16) + 4))),
            BinaryPrimitives.ReadSingleLittleEndian(source: bytes.AsSpan(start: ((row * 16) + 8))),
            BinaryPrimitives.ReadSingleLittleEndian(source: bytes.AsSpan(start: ((row * 16) + 12))));
        static void Near(Vector4 expected, Vector4 actual) => Assert.True(condition: (Vector4.Distance(value1: expected, value2: actual) <= 1e-6f),
            userMessage: $"Expected the acquired-image reduction {expected}, got {actual}.");
    }
}
