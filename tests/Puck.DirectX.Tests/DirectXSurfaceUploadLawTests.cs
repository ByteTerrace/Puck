using System.Runtime.Versioning;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Laws for the device's surface upload (<see cref="IGpuSurfaceUpload"/> on Direct3D 12): uploads of an
/// unchanged extent and format reuse the texture and hand back the same image view, allocating nothing once warm, and
/// an upload that rebuilds the texture hands back a new view naming it. An extent past Direct3D 12's two-dimensional
/// texture limit is refused by name before the current texture is touched. Each law runs on a software (WARP) device
/// without the debug layer and skips when the host has none that meets the device floor.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXSurfaceUploadLawTests {
    private const uint TextureLimit = 16384U;

    private static DirectXImageView ViewOf(nint handle) =>
        DirectXImageViews.Resolve(handle: handle)!;

    [Fact]
    public void ASteadyUploadReusesItsViewAndAllocatesNothing() {
        using var context = DirectXTestDevices.Warp(memory: null);
        using var upload = context.Services.SurfaceTransferFactory.CreateUpload();
        ReadOnlyMemory<byte> pixels = new byte[((8 * 8) * 4)];

        nint Steady() => upload.Upload(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: 8U,
            pixels: pixels,
            width: 8U
        );

        var first = Steady();

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => _ = Steady()),
            expected: 0L
        );
        Assert.Equal(
            actual: Steady(),
            expected: first
        );
    }
    [Fact]
    public void AnUploadThatRebuildsTheTextureHandsBackAViewOfIt() {
        using var context = DirectXTestDevices.Warp(memory: null);
        using var upload = context.Services.SurfaceTransferFactory.CreateUpload();
        var small = upload.Upload(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: 4U,
            pixels: new byte[((4 * 4) * 4)],
            width: 4U
        );
        var smallFormat = ViewOf(handle: small).Format;
        var large = upload.Upload(
            format: GpuPixelFormat.B8G8R8A8Unorm,
            height: 16U,
            pixels: new byte[((16 * 16) * 4)],
            width: 16U
        );
        var largeView = ViewOf(handle: large);

        Assert.Equal(
            actual: (smallFormat, largeView.Format),
            expected: (DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: GpuPixelFormat.R8G8B8A8Unorm), DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: GpuPixelFormat.B8G8R8A8Unorm))
        );
        Assert.NotEqual(
            actual: largeView.ResourceHandle,
            expected: 0
        );
    }
    [Fact]
    public void AnOversizedUploadIsRefusedByNameAndLeavesTheCurrentViewResolving() {
        using var context = DirectXTestDevices.Warp(memory: null);
        using var upload = context.Services.SurfaceTransferFactory.CreateUpload();
        var first = upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4);

        var wide = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, pixels: new byte[((TextureLimit + 1) * 4)], width: (TextureLimit + 1)));
        var tall = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: (TextureLimit + 1), pixels: new byte[((TextureLimit + 1) * 4)], width: 1));

        Assert.Equal(
            actual: (wide.ParamName, tall.ParamName),
            expected: ("width", "height")
        );
        Assert.Contains(
            actualString: wide.Message,
            expectedSubstring: $"{TextureLimit} texels"
        );
        Assert.Contains(
            actualString: tall.Message,
            expectedSubstring: $"{TextureLimit} texels"
        );
        Assert.NotNull(@object: DirectXImageViews.Resolve(handle: first));
        Assert.Equal(
            actual: upload.Upload(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, pixels: new byte[((4 * 4) * 4)], width: 4),
            expected: first
        );
    }
}
