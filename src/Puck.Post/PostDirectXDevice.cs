using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.DirectX.Presentation;
using Puck.Hosting;
using Puck.Vulkan.Interfaces;

namespace Puck.Post;

/// <summary>
/// The Tier-C cross-backend device: a bespoke Direct3D 12 device LUID-matched to the Vulkan host adapter (so a shared
/// resource is openable on both) plus the Direct3D 12 neutral <c>IGpuCompute*</c> services and a host context that
/// publishes the device. Ported from the demo's <c>DirectXComputeWorldDevice</c>/<c>DirectXComputeWorldServices</c>
/// (the worked reference). The debug layer stays OFF — on this machine <c>EnableDebugLayer</c> breaks the next
/// <c>D3D12CreateDevice</c>. Owned by <see cref="PostContext"/>, created lazily on the first Tier-C stage and shared
/// across the tier (each acquire waits the device idle, the "explicit reset between stages" seam).
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class PostDirectXDevice : IDisposable {
    private readonly DirectXDeviceContext m_deviceContext;
    private readonly ServiceProvider m_provider;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="PostDirectXDevice"/> class.</summary>
    /// <param name="hostProvider">The application service provider, from which the live Vulkan device (for the adapter LUID) is resolved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hostProvider"/> is <see langword="null"/>.</exception>
    public PostDirectXDevice(IServiceProvider hostProvider) {
        ArgumentNullException.ThrowIfNull(hostProvider);

        var vulkanDeviceContext = hostProvider.GetRequiredService<IVulkanDeviceContext>();
        var physicalDeviceApi = hostProvider.GetRequiredService<IVulkanPhysicalDeviceApi>();

        m_deviceContext = new DirectXDeviceContext(
            adapterLuidProvider: () => physicalDeviceApi.GetDeviceLuid(
                instanceHandle: vulkanDeviceContext.Instance.Handle,
                physicalDeviceHandle: vulkanDeviceContext.PhysicalDevice.Handle
            ),
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        );

        var services = new ServiceCollection();

        // The canonical Direct3D 12 compute seam plus the factories the host presenter would otherwise contribute —
        // this is an off-host, presenter-less collection, so they have no other source here.
        services.AddDirectXComputeApis();
        services.TryAddSingleton<IGpuDescriptorAllocator>(static _ => new DirectXGpuDescriptorAllocator());
        services.TryAddSingleton<IGpuQueueSubmitter>(static _ => new DirectXGpuQueueSubmitter());
        services.TryAddSingleton<IGpuShaderModuleFactory>(static _ => new DirectXGpuShaderModuleFactory());
        services.TryAddSingleton<IGpuStorageBufferFactory>(static _ => new DirectXGpuStorageBufferFactory());
        services.TryAddSingleton<IGpuSurfaceTransferFactory>(static _ => new DirectXGpuSurfaceTransferFactory());

        m_provider = services.BuildServiceProvider();
        Host = new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IGpuDeviceContext)] = m_deviceContext,
        });
    }

    /// <summary>Gets the bespoke Direct3D 12 device context on the LUID-matched adapter.</summary>
    public IGpuDeviceContext DeviceContext => m_deviceContext;
    /// <summary>Gets the host context that publishes the Direct3D 12 device as the shared <see cref="IGpuDeviceContext"/>.</summary>
    public IHostContext Host { get; }
    /// <summary>Gets the service provider holding the Direct3D 12 neutral compute services.</summary>
    public IServiceProvider Services => m_provider;

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_provider.Dispose();
        m_deviceContext.Dispose();
    }
}
