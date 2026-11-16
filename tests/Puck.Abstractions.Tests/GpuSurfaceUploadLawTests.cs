using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>The surface upload counts a copy only once its fence proves the copy ran. A copy whose batch never reached the
/// queue (its fence reports <see cref="GpuSubmissionAbandonedException"/>) neither releases the image it was to retire,
/// whose last samplers it proves nothing about, nor leaves its image in the layout the copy would have: the next copy
/// records from the layout the image is really in. Runs on the fake device, whose next fenced submission can be
/// abandoned.</summary>
public sealed class GpuSurfaceUploadLawTests {
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;

    [Fact]
    public void AnAbandonedCopyNeitherReleasesTheImageItRetiresNorClaimsItsLayout() {
        var gpu = new FakeGpuDevice(trackObjects: true) { DistinctImages = true, ImageTransitions = [] };
        using var upload = new GpuSurfaceUpload(deviceContext: gpu, device: static () => HeldDevice.Instance);
        var small = new byte[((4 * 4) * 4)];
        var large = new byte[((8 * 8) * 4)];

        _ = upload.Upload(format: Format, height: 4, pixels: small, width: 4);
        _ = upload.Upload(format: Format, height: 4, pixels: small, width: 4);

        var replaced = gpu.Created.First(predicate: static created => (created.Kind == "image"));

        // The resize retires the first image behind the copy into the second, and that copy is abandoned.
        gpu.AbandonNextFencedSubmission = true;
        _ = upload.Upload(format: Format, height: 8, pixels: large, width: 8);

        var replacement = upload.ImageHandle;

        gpu.ImageTransitions!.Clear();
        _ = upload.Upload(format: Format, height: 8, pixels: large, width: 8);

        Assert.Equal(expected: 0, actual: replaced.DisposeCount);
        Assert.Equal(
            expected: (replacement, GpuImageLayout.Undefined, GpuImageLayout.TransferDestination),
            actual: gpu.ImageTransitions[0]
        );

        // The next copy is proven: the replaced image goes, and the copy after it records from ShaderReadOnly.
        gpu.ImageTransitions.Clear();
        _ = upload.Upload(format: Format, height: 8, pixels: large, width: 8);

        Assert.Equal(expected: 1, actual: replaced.DisposeCount);
        Assert.Equal(
            expected: (replacement, GpuImageLayout.ShaderReadOnly, GpuImageLayout.TransferDestination),
            actual: gpu.ImageTransitions[0]
        );
    }

    private sealed class HeldDevice : IGpuHeldDevice {
        public static HeldDevice Instance { get; } = new();

        public bool IsDisposed => false;
    }
}
