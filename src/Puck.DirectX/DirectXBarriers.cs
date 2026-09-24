using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Builds the backend's resource barriers. Every transition the backend records goes through
/// <see cref="Transition"/>, so every one covers the whole resource and carries no split-barrier flag.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXBarriers {
    /// <summary>Builds a transition of every subresource of <paramref name="resource"/> from
    /// <paramref name="before"/> to <paramref name="after"/>, recorded whole (<c>D3D12_RESOURCE_BARRIER_FLAG_NONE</c>).</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="before">The state the resource is in.</param>
    /// <param name="after">The state the resource moves to.</param>
    /// <returns>The barrier, for <c>ResourceBarrier</c>.</returns>
    public static D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        var barrier = new D3D12_RESOURCE_BARRIER {
            Flags = D3D12_RESOURCE_BARRIER_FLAGS.D3D12_RESOURCE_BARRIER_FLAG_NONE,
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            StateAfter = after,
            StateBefore = before,
            Subresource = DirectXConstants.AllSubresources,
            pResource = resource,
        };

        return barrier;
    }
}
