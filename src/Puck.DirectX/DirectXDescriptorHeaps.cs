using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Creates the backend's descriptor heaps. Every heap the backend creates goes through this class, so every one is
/// created on node mask zero (the single adapter) and differs only in its type, size and shader visibility. A
/// CPU-only heap (<see cref="Create"/>) is any owner's; a shader-visible heap (<see cref="CreateShaderVisible"/>) is
/// created only for a device's own pair, <see cref="DirectXShaderVisibleHeaps"/>, and counted where it is created, so
/// no owner keeps shader-visible descriptors outside the ranges the device's heaps admit.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXDescriptorHeaps {
    /// <summary>Creates a CPU-only descriptor heap: a render-target, depth-stencil or staging heap whose descriptors
    /// shaders never read directly.</summary>
    /// <param name="device">The device.</param>
    /// <param name="type">The kind of descriptor the heap holds.</param>
    /// <param name="count">How many descriptors it holds.</param>
    /// <returns>The heap, owned by the caller.</returns>
    public static ID3D12DescriptorHeap* Create(ID3D12Device* device, D3D12_DESCRIPTOR_HEAP_TYPE type, uint count) =>
        CreateHeap(
            count: count,
            device: device,
            flags: D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_NONE,
            type: type
        );
    /// <summary>Creates a shader-visible descriptor heap (<c>D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE</c>) and counts
    /// it. Only a device's <see cref="DirectXShaderVisibleHeaps"/> calls it, once per heap of its pair.</summary>
    /// <param name="device">The device.</param>
    /// <param name="type">The kind of descriptor the heap holds: <c>CBV_SRV_UAV</c> or <c>SAMPLER</c>.</param>
    /// <param name="count">How many descriptors it holds.</param>
    /// <param name="created">The device's count of shader-visible heaps created, raised by one when the heap is
    /// created.</param>
    /// <returns>The heap, owned by the caller.</returns>
    public static ID3D12DescriptorHeap* CreateShaderVisible(ID3D12Device* device, D3D12_DESCRIPTOR_HEAP_TYPE type, uint count, ref long created) {
        var heap = CreateHeap(
            count: count,
            device: device,
            flags: D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
            type: type
        );

        _ = Interlocked.Increment(location: ref created);

        return heap;
    }

    private static ID3D12DescriptorHeap* CreateHeap(ID3D12Device* device, D3D12_DESCRIPTOR_HEAP_TYPE type, uint count, D3D12_DESCRIPTOR_HEAP_FLAGS flags) {
        var description = new D3D12_DESCRIPTOR_HEAP_DESC {
            Flags = flags,
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
