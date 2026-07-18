using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Exposes the core Direct3D 12 objects a host establishes — the device and its direct command queue — as a
/// single shared context for the producers and hosts built on top of them. This is the Direct3D 12 peer of
/// <c>IVulkanDeviceContext</c>: published through the host capability seam so a DirectX node resolves its
/// device the same way a Vulkan node resolves its own, letting a developer choose the backend without the
/// node layer changing.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public interface IDirectXDeviceContext {
    /// <summary>Gets the packed LUID (<c>ID3D12Device::GetAdapterLuid</c>) of the adapter the device was created on —
    /// the value a foreign backend matches to share resources on the same GPU. Additive: Puck.World's D3D12-host GPU
    /// capture transport opens its window/monitor capture on this adapter so its shared textures import cross-API.</summary>
    long AdapterLuid { get; }
    /// <summary>Gets the native <c>ID3D12CommandQueue</c> handle work is submitted to.</summary>
    nint CommandQueueHandle { get; }
    /// <summary>Gets the owning Direct3D 12 device.</summary>
    DirectXDevice Device { get; }
    /// <summary>Gets the feature level the device was created with.</summary>
    DirectXFeatureLevel FeatureLevel { get; }
    /// <summary>Gets a value indicating whether the context has been fully initialized.</summary>
    bool IsInitialized { get; }
}
