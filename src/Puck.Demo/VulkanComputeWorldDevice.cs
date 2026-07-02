using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// Bootstraps the infrastructure a compute producer needs to run on Vulkan while a Direct3D 12 window hosts: a bespoke
/// surface-less Vulkan device LUID-matched to the Direct3D 12 host adapter (so the host-owned shared image is openable
/// on both), the Vulkan neutral <c>IGpuCompute*</c> services, and a host context that publishes the device. The mirror
/// of <see cref="DirectXComputeWorldDevice"/>, with the LUID direction inverted: the FORWARD path reads the Vulkan
/// host's adapter LUID and pins a bespoke Direct3D 12 producer to it; here the Direct3D 12 host's adapter LUID
/// (<c>ID3D12Device::GetAdapterLuid</c>) pins the bespoke Vulkan producer. Owns the device and the service provider;
/// dispose when finished.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class VulkanComputeWorldDevice : IDisposable {
    private readonly VulkanComputeDeviceContext m_deviceContext;
    private readonly ServiceProvider m_provider;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="VulkanComputeWorldDevice"/> class.</summary>
    /// <param name="hostProvider">The application service provider, from which the live Direct3D 12 host device (for the adapter LUID) is resolved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hostProvider"/> is <see langword="null"/>.</exception>
    public VulkanComputeWorldDevice(IServiceProvider hostProvider) {
        ArgumentNullException.ThrowIfNull(hostProvider);

        // Read the Direct3D 12 HOST adapter's LUID (the inverse of the forward path, which reads the Vulkan host's) and
        // pin the bespoke Vulkan producer to the same GPU. Resolving Device realizes the lazily-created host device.
        var hostDevice = hostProvider.GetRequiredService<IDirectXDeviceContext>();
        var hostAdapterLuid = new DirectXNativeDeviceApi().GetAdapterLuid(deviceHandle: hostDevice.Device.Handle);

        m_provider = new ServiceCollection().AddVulkanComputeWorld().BuildServiceProvider();
        m_deviceContext = VulkanComputeDeviceContext.Create(adapterLuid: hostAdapterLuid, services: m_provider);
        Host = new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IGpuDeviceContext)] = m_deviceContext,
        });
    }

    /// <summary>Gets the bespoke surface-less Vulkan device context on the LUID-matched adapter.</summary>
    public IGpuDeviceContext DeviceContext => m_deviceContext;
    /// <summary>Gets the host context that publishes the Vulkan device as the shared <see cref="IGpuDeviceContext"/>.</summary>
    public IHostContext Host { get; }
    /// <summary>Gets the service provider holding the Vulkan neutral compute services.</summary>
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
