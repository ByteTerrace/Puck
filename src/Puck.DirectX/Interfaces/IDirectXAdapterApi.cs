using System.Runtime.Versioning;
using Puck.DirectX.Messages;

namespace Puck.DirectX.Interfaces;

/// <summary>
/// Wraps the DXGI entry points used to enumerate the graphics adapters visible to the system.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public interface IDirectXAdapterApi {
    /// <summary>Enumerates the graphics adapters visible to DXGI, in the platform's preference order.</summary>
    /// <returns>A description of each available adapter.</returns>
    /// <exception cref="DirectXException">A DXGI call failed.</exception>
    IReadOnlyList<DirectXAdapterDescription> EnumerateAdapters();
}
