using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The device's surface upload and readback (<see cref="GpuSurfaceUpload"/>, <see cref="GpuSurfaceReadback"/>)
/// round-trip a known pattern byte for byte on both backends: rows whose length is no multiple of Direct3D 12's 256-byte
/// copy pitch, one-, four- and eight-byte texels, a steady upload that rewrites the staging buffer after its previous
/// copy, and a rebuild that replaces the image and retires the one it replaced. Each readback reads the image the upload
/// left in <see cref="GpuImageLayout.ShaderReadOnly"/>, through a copy submitted after the upload's on the same
/// queue.</summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class GpuSurfaceTransferDeviceLawTests {
    [Fact]
    public void VulkanRoundTripsEveryPatternByteForByte() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(GpuSurfaceTransferDeviceLawTests));

        RoundTrip(services: device.Services);
    }
    [Fact]
    public void DirectXRoundTripsEveryPatternByteForByte() {
        using var device = DirectXTestDevices.Hardware();

        RoundTrip(services: device.Services);
    }

    private static void RoundTrip(GpuDeviceServices services) {
        using var upload = ((GpuSurfaceUpload)services.SurfaceTransferFactory.CreateUpload());
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        // (format, width, height, seed): a padded row (37 four-byte texels, 148 bytes), the same extent again with
        // another pattern (the steady path), single-byte and eight-byte texels at odd widths (rebuilds), and a row
        // already a whole pitch (64 four-byte texels, 256 bytes).
        (GpuPixelFormat Format, uint Width, uint Height, int Seed)[] cases = [
            (GpuPixelFormat.R8G8B8A8Unorm, 37U, 5U, 1),
            (GpuPixelFormat.R8G8B8A8Unorm, 37U, 5U, 2),
            (GpuPixelFormat.R8Unorm, 129U, 3U, 3),
            (GpuPixelFormat.R16G16B16A16Float, 19U, 7U, 4),
            (GpuPixelFormat.B8G8R8A8Unorm, 64U, 4U, 5),
        ];

        foreach (var (format, width, height, seed) in cases) {
            var pattern = Pattern(
                format: format,
                height: height,
                seed: seed,
                width: width
            );

            _ = upload.Upload(
                format: format,
                height: height,
                pixels: pattern,
                width: width
            );

            var read = readback.Read(
                format: format,
                height: height,
                sourceImageHandle: upload.ImageHandle,
                sourceLayout: GpuImageLayout.ShaderReadOnly,
                width: width
            );

            Assert.Equal(
                actual: read.ToArray(),
                expected: pattern
            );
        }
    }
    // Every byte a function of its offset and the seed, so a row shifted, a pad byte copied or a stale upload read all
    // differ. A half-float texel keeps its high bytes below 0x7C, which no finite value exceeds, so the image's bytes
    // survive the copy unchanged (no NaN canonicalization).
    private static byte[] Pattern(GpuPixelFormat format, uint width, uint height, int seed) {
        var bytes = new byte[checked((int)GpuPixelFormats.ImageByteLength(
            format: format,
            height: height,
            width: width
        ))];

        for (var index = 0; (index < bytes.Length); index++) {
            var value = ((byte)(((index * 31) + (seed * 97)) ^ (index >> 8)));

            bytes[index] = (((format == GpuPixelFormat.R16G16B16A16Float) && ((index & 1) == 1))
                ? ((byte)(value % 0x7C))
                : value);
        }

        return bytes;
    }
}
