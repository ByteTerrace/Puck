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
}
