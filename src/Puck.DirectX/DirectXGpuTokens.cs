using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Tracks mutable GPU state across begin/end pairs for a single command buffer. Stored in a
/// <see cref="GCHandle"/> so the command recorder can update it without knowing the render target type.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXCommandBufferState {
    internal List<IDisposable> RetainedResources { get; } = [];

    /// <summary>Gets the buffer states explicit transitions recorded in this command list's current recording; the recorder resets it when a recording begins.</summary>
    public DirectXBufferStates BufferStates { get; } = new();

    internal void ReleaseRetainedResources() {
        foreach (var resource in RetainedResources) { resource.Dispose(); }
        RetainedResources.Clear();
    }

    public nint Allocator;
    public nint CommandList;

    /// <summary>Gets or sets the framebuffer of the render pass being recorded, which its end transitions; null outside
    /// a render pass.</summary>
    public DirectXGpuFramebuffer? CurrentFramebuffer { get; set; }

    public static DirectXCommandBufferState Decode(nint commandBufferHandle) =>
        ((DirectXCommandBufferState)GCHandle.FromIntPtr(value: commandBufferHandle).Target!);
}
/// <summary>
/// Packages a pipeline state object and its root signature alongside the parameter-index metadata the command
/// recorder needs to call <c>SetGraphicsRootDescriptorTable</c> and <c>SetGraphicsRoot32BitConstants</c>.
/// Stored in a <see cref="GCHandle"/>; the same token is returned for
/// <see cref="IGpuPipeline.Handle"/>, <see cref="IGpuPipeline.LayoutHandle"/>, and
/// <see cref="IGpuPipeline.DescriptorSetLayoutHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXPipelineLayout : IDisposable {
    public nint PsoHandle;
    public nint RootSignatureHandle;
    public byte[] RootSignatureBlob = [];
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
    /// offsets and <see cref="DirectXGpuBindings"/>'s writes both go through this map, keeping them in lockstep.</summary>
    public uint[] SlotByBinding = [];

    internal static DirectXPipelineLayout CreateForParameters(bool hasDescriptorTable, GpuPushConstantBinding? pushConstantBinding) {
        var hasRootConstants = (pushConstantBinding is not null);
        var layout = new DirectXPipelineLayout();

        if (hasDescriptorTable) {
            layout.DescriptorTableParamIndex = 0;
            layout.RootConstantsParamIndex = (hasRootConstants
                ? 1
                : -1
            );
        } else {
            layout.RootConstantsParamIndex = (hasRootConstants
                ? 0
                : -1
            );
        }

        if (hasRootConstants) {
            layout.RootConstantsCount = ((pushConstantBinding!.Size + 3) / 4);
        }

        return layout;
    }

    /// <inheritdoc/>
    public void Dispose() {
        DirectXConstants.Release(pointer: ref PsoHandle);
        DirectXConstants.Release(pointer: ref RootSignatureHandle);
    }
}
/// <summary>
/// A descriptor pool: one range of its device's shader-visible CBV_SRV_UAV heap
/// (<see cref="DirectXShaderVisibleHeaps"/>), with the range's base addresses and the descriptor increment needed to
/// write descriptors without re-querying the device. Stored in a <see cref="GCHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXDescriptorPool {
    /// <summary>The range the heap admitted, or <see langword="null"/> once returned (or for a pool of no descriptor,
    /// which holds an empty admission).</summary>
    public GpuDescriptorAdmission? Admission;
    /// <summary>The device heaps the range belongs to.</summary>
    public DirectXShaderVisibleHeaps? Heaps;
    public uint DescriptorSize;
    public uint Capacity;
    public nuint CpuBase;
    public ulong GpuBase;
    /// <summary>The next free heap slot; <c>AllocateSet</c> bump-allocates each set's region from here, so multiple
    /// independent sets can share one pool (one range of the device's view heap) without overlapping — like a Vulkan pool.</summary>
    public uint NextOffset;
}
/// <summary>
/// A range inside a <see cref="DirectXDescriptorPool"/>'s range of the device's view heap, allocated once via
/// <c>IGpuBindings.AllocateSet</c>. Stored in a <see cref="GCHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXDescriptorSet {
    public uint DescriptorSize;
    public nuint CpuBase;
    public ulong GpuBase;
    /// <summary>The owning layout's <see cref="DirectXPipelineLayout.SlotByBinding"/> packing, so each descriptor write
    /// lands at the same packed heap slot the root signature's range for that binding points at.</summary>
    public uint[] SlotByBinding = [];
}
/// <summary>
/// Pairs an <c>ID3D12Resource*</c> with its DXGI format so <see cref="DirectXGpuBindings"/> can create a typed SRV
/// without calling the problematic <c>GetDesc</c> vtable slot. Stored in a <see cref="GCHandle"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXImageView {
    public nint ResourceHandle;
    public DXGI_FORMAT Format;
}
/// <summary>The textures created with <c>ALLOW_SIMULTANEOUS_ACCESS</c>, keyed by <c>ID3D12Resource*</c>: the compute
/// recorder keeps these in the <c>COMMON</c> layout, the only one such a texture may hold under Enhanced Barriers.
/// The creating image registers on construction and withdraws on dispose.</summary>
public static class DirectXSimultaneousAccessResources {
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, byte> Resources = new();

    public static bool Contains(nint resourceHandle) => Resources.ContainsKey(key: resourceHandle);
    public static void Register(nint resourceHandle) => Resources[resourceHandle] = 0;
    public static void Withdraw(nint resourceHandle) => _ = Resources.TryRemove(
        key: resourceHandle,
        value: out _
    );
}
