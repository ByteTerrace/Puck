using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.Testing;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>The Direct3D 12 peer of the Vulkan device-ownership laws: a surface upload, readback or shared-surface
/// import (<see cref="DirectXDeviceOwnership"/>) stays on the device it first created its objects on. A device context
/// replaces its device in place on a loss (<see cref="DirectXDeviceContext.Recreate"/>), so a transfer object its owner
/// did not release across the loss is handed the replacement through the same context: it refuses that device, and its
/// release after the device it holds was released, by name. Each law runs on a software (WARP) device and skips when the
/// host has none.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXDeviceOwnershipLawTests {
    private const uint Extent = 4U;

    [Fact]
    public void AnUploadOutlivingARecreateRefusesTheReplacementAndItsLateRelease() {
        using var context = DirectXTestDevices.Warp();
        var upload = new DirectXSurfaceUpload(deviceContext: context);
        var pixels = new byte[((Extent * Extent) * 4U)];

        upload.Upload(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            pixels: pixels,
            width: Extent
        );
        context.Recreate();

        var other = Assert.Throws<InvalidOperationException>(testCode: () => upload.Upload(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            pixels: pixels,
            width: Extent
        ));
        var late = Assert.Throws<InvalidOperationException>(testCode: upload.Dispose);

        Assert.StartsWith(
            actualString: other.Message,
            expectedStartString: $"A {nameof(DirectXSurfaceUpload)} was handed a device other"
        );
        Assert.StartsWith(
            actualString: late.Message,
            expectedStartString: $"A {nameof(DirectXSurfaceUpload)} was released after its device was destroyed"
        );
    }
    [Fact]
    public void AnUploadReleasedBeforeItsDeviceGoesIsReleasedAndTheNextOneWorksOnTheReplacement() {
        using var context = DirectXTestDevices.Warp();
        var pixels = new byte[((Extent * Extent) * 4U)];

        using (var upload = new DirectXSurfaceUpload(deviceContext: context)) {
            upload.Upload(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: Extent,
                pixels: pixels,
                width: Extent
            );
        }

        context.Recreate();

        using var replacement = new DirectXSurfaceUpload(deviceContext: context);

        replacement.Upload(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            pixels: pixels,
            width: Extent
        );
    }
    [Fact]
    public void AReadbackOutlivingARecreateRefusesTheReplacementAndItsLateRelease() {
        using var context = DirectXTestDevices.Warp();
        var readback = new DirectXGpuSurfaceTransferFactory(deviceContext: context).CreateReadback();
        var source = Source(context: context);

        try {
            Assert.Equal(
                actual: Read(
                    readback: readback,
                    source: source
                ),
                expected: ((int)((Extent * Extent) * 4U))
            );
        } finally {
            _ = ((IUnknown*)source)->Release();
        }

        context.Recreate();

        var replacementSource = Source(context: context);

        try {
            var other = Assert.Throws<InvalidOperationException>(testCode: () => Read(
                readback: readback,
                source: replacementSource
            ));

            Assert.StartsWith(
                actualString: other.Message,
                expectedStartString: "A DirectXGpuSurfaceReadback was handed a device other"
            );
        } finally {
            _ = ((IUnknown*)replacementSource)->Release();
        }

        var late = Assert.Throws<InvalidOperationException>(testCode: readback.Dispose);

        Assert.StartsWith(
            actualString: late.Message,
            expectedStartString: "A DirectXGpuSurfaceReadback was released after its device was destroyed"
        );
    }

    private static int Read(IGpuSurfaceReadback readback, ID3D12Resource* source) =>
        readback.Read(
            bytesPerPixel: 4U,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Extent,
            sourceImageHandle: ((nint)source),
            sourceLayout: GpuImageLayout.External,
            width: Extent
        ).Length;
    // A small texture in the common state, the External layout's, on the context's current device.
    private static ID3D12Resource* Source(DirectXDeviceContext context) =>
        DirectXTextures.CreateCommitted(
            device: ((ID3D12Device*)context.Device.Handle),
            format: DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            height: Extent,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            width: Extent
        );
}
