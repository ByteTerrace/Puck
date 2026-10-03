using System.Reflection;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX.Interop;
using Puck.DirectX.Presentation;
using Puck.Shaders;
using Puck.Testing;
using Windows.Win32.Graphics.Dxgi.Common;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Device-free laws for the compositor's source descriptor cache. An uninitialized swap chain presents
/// nothing, so Blit exercises its real descriptor decisions using fake bindings without opening a device.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXSurfaceCompositorLawTests {
    [Fact]
    public void AReplacementAtTheSameResourceAddressRewritesTheSourceDescriptor() {
        var bindings = new FakeGpuDevice(countCalls: true);
        using var context = new DirectXDeviceContext();
        using var compositor = new DirectXSurfaceCompositor(
            commandListRecorder: new DirectXCommandListRecorder(),
            presentationOptions: new PresentationOptions(),
            pipelines: new GpuPassPipelineCache(),
            presentation: new PresentationWork(name: "presentation.directx")
        );

        typeof(DirectXSurfaceCompositor).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_bindings")!.SetValue(obj: compositor, value: bindings);
        const nint Resource = 0x1234;
        var first = DirectXImageViews.Register(view: new DirectXImageView { Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, ResourceHandle = Resource });
        nint next = 0;

        Surface Image(nint handle, GpuPixelFormat format) => Surface.SameDeviceImage(format: format, height: 4, imageHandle: Resource, imageViewHandle: handle, width: 4);

        try {
            compositor.Blit(deviceContext: context, surface: Image(format: GpuPixelFormat.R8G8B8A8Unorm, handle: first));
            compositor.Blit(deviceContext: context, surface: Image(format: GpuPixelFormat.R8G8B8A8Unorm, handle: first));
            Assert.Equal(expected: 1, actual: bindings.Count(key: "IGpuBindings.WriteSampledImage"));

            DirectXImageViews.Release(handle: first);
            next = DirectXImageViews.Register(view: new DirectXImageView { Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, ResourceHandle = Resource });
            compositor.Blit(deviceContext: context, surface: Image(format: GpuPixelFormat.B8G8R8A8Unorm, handle: next));
            Assert.Equal(expected: 2, actual: bindings.Count(key: "IGpuBindings.WriteSampledImage"));
            Assert.False(condition: context.IsInitialized);
        } finally {
            DirectXImageViews.Release(handle: first);
            DirectXImageViews.Release(handle: next);
        }
    }
}
