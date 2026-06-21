using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Tracks mutable GPU state across begin/end pairs for a single command buffer. Stored in a
/// <see cref="GCHandle"/> so the command recorder can update it without knowing the render target type.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXCommandBufferState {
    public nint Allocator;
    public nint CommandList;
    public nint CurrentRenderTargetHandle;
    public D3D12_RESOURCE_STATES RenderTargetState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET;
}

/// <summary>
/// Pairs a timestamp <c>ID3D12QueryHeap</c> with the READBACK buffer its resolved results land in, plus the query
/// capacity. Stored in a <see cref="GCHandle"/> so the neutral timing pool handle is the GCHandle pointer, decoded
/// by the timing recorder the same way command-buffer state is.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXTimingPoolState {
    public nint QueryHeapHandle;
    public nint ReadbackBufferHandle;
    public uint Capacity;
}

/// <summary>
/// Packages a pipeline state object and its root signature alongside the parameter-index metadata the command
/// recorder needs to call <c>SetGraphicsRootDescriptorTable</c> and <c>SetGraphicsRoot32BitConstants</c>.
/// Stored in a <see cref="GCHandle"/>; the same token is returned for
/// <see cref="IGpuPipeline.Handle"/>, <see cref="IGpuPipeline.LayoutHandle"/>, and
/// <see cref="IGpuPipeline.DescriptorSetLayoutHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXPipelineLayout {
    public nint PsoHandle;
    public nint RootSignatureHandle;
    public int DescriptorTableParamIndex = -1;
    public int RootConstantsParamIndex = -1;
    public uint RootConstantsCount;
    /// <summary>The total number of heap slots this set's descriptor table occupies once bindings are packed, so
    /// <c>AllocateSet</c> can hand each set from a shared pool its own non-overlapping heap region.</summary>
    public uint DescriptorSlotCount;
    /// <summary>Maps each binding index to the base heap slot it was packed to (indexed by <c>GpuComputeBinding.Binding</c>;
    /// an array binding occupies <c>Count</c> consecutive slots from there). Heap slots are packed in binding-list order
    /// rather than equated to the binding index, so an array binding never collides with a later binding regardless of
    /// the chosen index values — the binding index is a logical id, not a heap offset. The root signature's range
    /// offsets and the descriptor allocator's writes both go through this map, keeping them in lockstep.</summary>
    public uint[] SlotByBinding = [];
}

/// <summary>
/// Encodes a shader-visible CBV_SRV_UAV descriptor heap together with the cached base addresses and
/// descriptor increment size needed to write and bind descriptors without re-querying the device.
/// Stored in a <see cref="GCHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXDescriptorPool {
    public nint HeapHandle;
    public uint DescriptorSize;
    public uint Capacity;
    public nuint CpuBase;
    public ulong GpuBase;
    /// <summary>The next free heap slot; <c>AllocateSet</c> bump-allocates each set's region from here, so multiple
    /// independent sets can share one pool (one shader-visible heap) without overlapping — like a Vulkan pool.</summary>
    public uint NextOffset;
}

/// <summary>
/// A range inside a <see cref="DirectXDescriptorPool"/>'s heap, allocated once via
/// <c>IGpuDescriptorAllocator.AllocateSet</c>. Stored in a <see cref="GCHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXDescriptorSet {
    public nint HeapHandle;
    public uint DescriptorSize;
    public nuint CpuBase;
    public ulong GpuBase;
    /// <summary>The owning layout's <see cref="DirectXPipelineLayout.SlotByBinding"/> packing, so each descriptor write
    /// lands at the same packed heap slot the root signature's range for that binding points at.</summary>
    public uint[] SlotByBinding = [];
}

/// <summary>
/// Holds the three values needed to fill a <c>D3D12_VERTEX_BUFFER_VIEW</c>. Stored in a
/// <see cref="GCHandle"/> so the raw pointer can serve as <see cref="IGpuVertexBuffer.BufferHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXVertexBufferView {
    public ulong BufferLocation;
    public uint SizeBytes;
    public uint StrideBytes;
}

/// <summary>
/// Pairs an <c>ID3D12Resource*</c> with its DXGI format so the descriptor allocator can create a typed SRV
/// without calling the problematic <c>GetDesc</c> vtable slot. Stored in a <see cref="GCHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXImageView {
    public nint ResourceHandle;
    public DXGI_FORMAT Format;
}
