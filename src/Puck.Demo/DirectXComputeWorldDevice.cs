using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Vulkan.Interfaces;

namespace Puck.Demo;

/// <summary>
/// Bootstraps the infrastructure a compute producer needs to run on Direct3D 12 while a Vulkan window hosts: a
/// bespoke Direct3D 12 device LUID-matched to the Vulkan host adapter (so a shared resource is openable on both),
/// the Direct3D 12 neutral <c>IGpuCompute*</c> services, and a host context that publishes the device. The
/// cross-backend present wrapper and the parity gate both build on it, so the device/service bootstrap lives in one
/// place. Owns the device and the service provider; dispose when finished.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class DirectXComputeWorldDevice : IDisposable {
    private readonly DirectXDeviceContext m_deviceContext;
    private readonly ServiceProvider m_provider;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="DirectXComputeWorldDevice"/> class.</summary>
    /// <param name="hostProvider">The application service provider, from which the live Vulkan device (for the adapter LUID) is resolved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hostProvider"/> is <see langword="null"/>.</exception>
    public DirectXComputeWorldDevice(IServiceProvider hostProvider) {
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

        var services = new ServiceCollection().AddDirectXComputeWorld();

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
