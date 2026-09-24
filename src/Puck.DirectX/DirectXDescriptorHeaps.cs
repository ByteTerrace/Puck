using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Creates the backend's descriptor heaps. Every heap the backend creates goes through <see cref="Create"/>, so every
/// one is created on node mask zero (the single adapter) and differs only in its type, size and shader visibility.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXDescriptorHeaps {
    /// <summary>Creates a descriptor heap.</summary>
    /// <param name="device">The device.</param>
    /// <param name="type">The kind of descriptor the heap holds.</param>
    /// <param name="count">How many descriptors it holds.</param>
    /// <param name="shaderVisible">Whether shaders read descriptors from it directly
    /// (<c>D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE</c>); <see langword="false"/> for a CPU-only heap, and always for a
    /// render-target heap.</param>
    /// <returns>The heap, owned by the caller.</returns>
    public static ID3D12DescriptorHeap* Create(ID3D12Device* device, D3D12_DESCRIPTOR_HEAP_TYPE type, uint count, bool shaderVisible) {
        var description = new D3D12_DESCRIPTOR_HEAP_DESC {
            Flags = (shaderVisible
                ? D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE
                : D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_NONE
            ),
            NodeMask = 0,
            NumDescriptors = count,
            Type = type,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in description,
            ppvHeap: out var heap,
            riid: ID3D12DescriptorHeap.IID_Guid
        );

        return ((ID3D12DescriptorHeap*)heap);
    }
}
