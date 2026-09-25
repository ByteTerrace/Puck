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
    /// <summary>The <see cref="GCHandle"/> of each group's <see cref="DirectXGroupLayout"/>, indexed by the group's
    /// ordinal and zero where the pipeline binds no group, for a pipeline created from a
    /// <see cref="GpuPipelineLayoutDescription"/>; empty for one created without it. <see cref="Dispose"/> frees
    /// them.</summary>
    public nint[] GroupHandles = [];

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

        for (var ordinal = 0; (ordinal < GroupHandles.Length); ordinal++) {
            if (0 != GroupHandles[ordinal]) {
                GCHandle.FromIntPtr(value: GroupHandles[ordinal]).Free();
                GroupHandles[ordinal] = 0;
            }
        }
    }
}
/// <summary>
/// One group of a pipeline created from a <see cref="GpuPipelineLayoutDescription"/>, as its root signature lays it
/// out (<see cref="DirectXRootLayout"/>): the view table's and the sampler table's root parameter indices, each table's
/// length in descriptors, and each binding's first descriptor in its table. A set of the group takes a region of its
/// pool's view range as long as the view table and a region of its sampler range as long as the sampler table. Stored in
/// a <see cref="GCHandle"/>, the handle <c>IGpuBindings.AllocateSet</c> takes for the group.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGroupLayout {
    /// <summary>Initializes a new instance of the <see cref="DirectXGroupLayout"/> class from a planned root layout.</summary>
    /// <param name="layout">The planned root layout.</param>
    /// <param name="group">The group.</param>
    public DirectXGroupLayout(DirectXRootLayout layout, GpuGroupLayoutDescription group) {
        ArgumentNullException.ThrowIfNull(argument: layout);
        ArgumentNullException.ThrowIfNull(argument: group);

        var views = layout.TableOf(
            kind: DirectXRootParameterKind.ViewTable,
            ordinal: group.Ordinal
        );
        var samplers = layout.TableOf(
            kind: DirectXRootParameterKind.SamplerTable,
            ordinal: group.Ordinal
        );
        var slots = new uint[(group.Bindings[^1].Binding + 1)];
        var kinds = new GpuBindingKind?[slots.Length];

        foreach (var binding in group.Bindings) {
            kinds[binding.Binding] = binding.Kind;
        }

        foreach (var table in ((ReadOnlySpan<DirectXRootParameter?>)[views, samplers])) {
            foreach (var range in (table?.Ranges ?? [])) {
                slots[range.BaseRegister] = range.TableOffset;
            }
        }

        KindByBinding = kinds;
        Ordinal = group.Ordinal;
        SamplerSlotCount = (samplers?.DescriptorCount ?? 0U);
        SamplerTableIndex = ((samplers is null)
            ? -1
            : ((int)samplers.Index));
        SlotByBinding = slots;
        ViewSlotCount = (views?.DescriptorCount ?? 0U);
        ViewTableIndex = ((views is null)
            ? -1
            : ((int)views.Index));
    }

    /// <summary>Gets each binding's kind, indexed by binding number, or <see langword="null"/> where the group declares
    /// no binding.</summary>
    public IReadOnlyList<GpuBindingKind?> KindByBinding { get; }
    /// <summary>Gets the group's ordinal.</summary>
    public uint Ordinal { get; }
    /// <summary>Gets the sampler table's length in descriptors, or zero when the group holds no sampler.</summary>
    public uint SamplerSlotCount { get; }
    /// <summary>Gets the sampler table's root parameter index, or -1 when the group holds no sampler.</summary>
    public int SamplerTableIndex { get; }
    /// <summary>Gets each binding's first descriptor in its table, the view table or the sampler table as its kind
    /// takes, indexed by binding number.</summary>
    public uint[] SlotByBinding { get; }
    /// <summary>Gets the view table's length in descriptors, or zero when the group holds only samplers.</summary>
    public uint ViewSlotCount { get; }
    /// <summary>Gets the view table's root parameter index, or -1 when the group holds only samplers.</summary>
    public int ViewTableIndex { get; }
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
    /// <summary>The sampler descriptors the pool's range of the device's sampler heap holds; zero for a pool of no
    /// sampler.</summary>
    public uint SamplerCapacity;
    /// <summary>The CPU handle of the pool's first sampler descriptor.</summary>
    public nuint SamplerCpuBase;
    /// <summary>The sampler heap's descriptor increment, in bytes.</summary>
    public uint SamplerDescriptorSize;
    /// <summary>The GPU handle of the pool's first sampler descriptor.</summary>
    public ulong SamplerGpuBase;
    /// <summary>The next free sampler slot, which <c>AllocateSet</c> bump-allocates a group's sampler table from as it
    /// does views from <see cref="NextOffset"/>.</summary>
    public uint SamplerNextOffset;

    /// <summary>The <see cref="System.Runtime.InteropServices.GCHandle"/> of every set <c>AllocateSet</c> placed in the
    /// pool, which <c>DestroyPool</c> frees with the pool's own, so a pool's sets release with it.</summary>
    public List<nint> SetHandles { get; } = [];
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
    /// <summary>The group a set of a pipeline created from a <see cref="GpuPipelineLayoutDescription"/> is allocated
    /// for, or <see langword="null"/> for a set of any other pipeline.</summary>
    public DirectXGroupLayout? Group;
    /// <summary>The CPU handle of the set's sampler table in the device's sampler heap, for a group holding a
    /// sampler.</summary>
    public nuint SamplerCpuBase;
    /// <summary>The sampler heap's descriptor increment, in bytes.</summary>
    public uint SamplerDescriptorSize;
    /// <summary>The GPU handle of the set's sampler table in the device's sampler heap, for a group holding a
    /// sampler.</summary>
    public ulong SamplerGpuBase;
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
