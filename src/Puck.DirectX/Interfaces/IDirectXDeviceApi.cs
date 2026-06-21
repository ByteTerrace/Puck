using System.Runtime.Versioning;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Wraps the Direct3D 12 device-creation entry points: probing an adapter's capabilities and creating owning
/// device wrappers from a hardware adapter or the software (WARP) renderer.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public interface IDirectXDeviceApi {
    /// <summary>Creates a Direct3D 12 device on the hardware adapter identified by a LUID.</summary>
    /// <param name="adapterLuid">The packed adapter LUID, as reported on a <see cref="Messages.DirectXAdapterDescription"/>.</param>
    /// <param name="minimumFeatureLevel">The minimum feature level the device must support.</param>
    /// <returns>An owning device wrapper.</returns>
    /// <exception cref="ArgumentException">No adapter with the given LUID was found.</exception>
    /// <exception cref="DirectXException">Device creation failed.</exception>
    DirectXDevice CreateDevice(long adapterLuid, DirectXFeatureLevel minimumFeatureLevel);
    /// <summary>Creates a Direct3D 12 device on the software (WARP) renderer, which is always available.</summary>
    /// <param name="minimumFeatureLevel">The minimum feature level the device must support.</param>
    /// <returns>An owning device wrapper.</returns>
    /// <exception cref="DirectXException">Device creation failed.</exception>
    DirectXDevice CreateWarpDevice(DirectXFeatureLevel minimumFeatureLevel);
    /// <summary>Probes the highest Direct3D 12 feature level the adapter identified by a LUID supports.</summary>
    /// <param name="adapterLuid">The packed adapter LUID, as reported on a <see cref="Messages.DirectXAdapterDescription"/>.</param>
    /// <returns>The highest supported feature level, or <see langword="null"/> if the adapter does not support Direct3D 12.</returns>
    /// <exception cref="ArgumentException">No adapter with the given LUID was found.</exception>
    DirectXFeatureLevel? ProbeMaxFeatureLevel(long adapterLuid);
    /// <summary>Reads the packed adapter LUID a created device was placed on (<c>ID3D12Device::GetAdapterLuid</c>).
    /// The Direct3D 12 peer of <c>IVulkanPhysicalDeviceApi.GetDeviceLuid</c>: the reverse cross-backend path reads a
    /// Direct3D 12 host's adapter LUID through this to LUID-match a bespoke Vulkan producer device to the same GPU.</summary>
    /// <param name="deviceHandle">The native <c>ID3D12Device</c> handle.</param>
    /// <returns>The adapter LUID, packed as <c>(HighPart &lt;&lt; 32) | LowPart</c> — directly comparable to the value <c>IVulkanPhysicalDeviceApi.GetDeviceLuid</c> returns.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> is zero.</exception>
    long GetAdapterLuid(nint deviceHandle);
    /// <summary>Reads <c>ID3D12Device::GetDeviceRemovedReason</c> — the specific HRESULT explaining a device removal
    /// (e.g. <c>DXGI_ERROR_DEVICE_HUNG</c> 0x887A0006 for a GPU timeout/too-much-work, <c>DXGI_ERROR_DEVICE_RESET</c>
    /// 0x887A0007, <c>DXGI_ERROR_DRIVER_INTERNAL_ERROR</c> 0x887A0020 for invalid GPU work / a page fault). Returns
    /// <c>S_OK</c> (0) when the device is healthy.</summary>
    /// <param name="deviceHandle">The native <c>ID3D12Device</c> handle (zero returns 0).</param>
    /// <returns>The removal-reason HRESULT.</returns>
    int GetDeviceRemovedReason(nint deviceHandle);
}
