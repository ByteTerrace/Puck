using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Laws for the device's surface upload (<see cref="IGpuSurfaceUpload"/> on Direct3D 12): uploads of an
/// unchanged extent and format reuse the texture and hand back the same image view, allocating nothing once warm, and
/// an upload that rebuilds the texture hands back a new view naming it. Each law runs on a software (WARP) device
/// without the debug layer and skips when the host has none that meets the device floor.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXSurfaceUploadLawTests {
    private static DirectXImageView ViewOf(nint handle) =>
        ((DirectXImageView)GCHandle.FromIntPtr(value: handle).Target!);

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
}
