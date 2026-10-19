using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// One Direct3D 12 device's two shader-visible descriptor heaps: a CBV/SRV/UAV heap of the device's reported
/// <see cref="GpuDeviceCapabilities.ViewHeapSize"/> and a sampler heap of the budget's
/// <see cref="GpuDescriptorHeapBudget.SamplerDescriptors"/> (the reported <see cref="GpuDeviceCapabilities.SamplerHeapSize"/>
/// held within <see cref="GpuDeviceCapabilities.StaticSamplerHeapSize"/>), created once with the device and released with it. Every
/// descriptor pool is a range of the view heap, and a pool holding samplers also a range of the sampler heap, admitted
/// through the device's <see cref="GpuDescriptorHeapBudget"/>, so a pool that does not fit is refused by name and the
/// heaps never grow; every command list binds both heaps once (<see cref="Bind"/>).
/// <para>A storage clear needs a descriptor in the bound view heap for its GPU handle and one in a CPU-only heap for its
/// CPU handle, so the device keeps <see cref="ClearDescriptors"/> of each: one range of the view heap, admitted with the
/// heaps, and a CPU-only heap of the same size whose slot <c>i</c> mirrors the range's slot <c>i</c>. A cleared resource
/// holds a slot for each view a clear has asked of it, written once (<see cref="ClearOf"/>), until the resource is
/// released (<see cref="ForgetClears"/>).</para>
/// <para>Both shader-visible heaps count under <see cref="GpuDeviceMemoryWork"/> as device-local allocations of their
/// descriptors at the device's increment (<see cref="GpuDescriptorHeapBudget.HeapBytes"/>), and
/// <see cref="Dispose"/> ends those entries before the device's teardown ends the device. Every member is safe to call
/// from any thread.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXShaderVisibleHeaps : IDisposable {
    /// <summary>The clear views one device holds at once, one for each view a clear has asked of a live resource: four
    /// pipeline nodes each clearing <c>ShaderPipelineLimits.MaxResources</c> storages, with room.</summary>
    public const uint ClearDescriptors = 4096U;
    /// <summary>The private-data key a cleared resource carries its clear tag under.</summary>
    public static readonly Guid ClearKey = new(g: "b3e1a7c4-5d92-4f08-8c6e-1a4f9d2b7e35");
    /// <summary>The name the device's clear range is admitted under.</summary>
    public const string ClearOwner = "storage clears";

    // Each cleared resource's clear views, by the tag it carries under ClearKey, never by its address, which a later
    // resource may reuse.
    private static readonly ConcurrentDictionary<long, List<(DirectXShaderVisibleHeaps Heaps, D3D12_UNORDERED_ACCESS_VIEW_DESC View, DirectXClearDescriptor Descriptor)>> Cleared = [];
    private static long ClearTags;

    private readonly GpuDescriptorHeapBudget m_budget;
    private readonly GpuDescriptorAdmission m_clearAdmission;
    private readonly GpuRangeAllocator m_clears;
    private readonly nint m_device;
    private readonly Lock m_gate = new();
    private readonly GpuDeviceMemoryWork? m_memory;

    private nint m_clearHeap;
    private nint m_samplerHeap;
    private nint m_viewHeap;

    private DirectXShaderVisibleHeaps(nint device, GpuDescriptorHeapBudget budget, GpuDeviceMemoryWork? memory) {
        m_budget = budget;
        m_device = device;
        m_memory = memory;
        m_clears = new GpuRangeAllocator(
            maxRanges: ((int)ClearDescriptors),
            size: ClearDescriptors
        );

        if (!budget.TryAdmit(
            admission: out var clears,
            owner: ClearOwner,
            pools: [new GpuDescriptorPoolSizes(
                MaxSets: 1U,
                StorageBufferCount: 0U,
                StorageImageCount: ClearDescriptors
            )],
            refusal: out var refusal
        )) {
            throw new GpuDescriptorHeapRefusalException(message: refusal);
        }

        m_clearAdmission = clears;
    }

    /// <summary>Gets the device's admission of descriptor pools into its view heap.</summary>
    public GpuDescriptorHeapBudget Budget => m_budget;
    /// <summary>Gets the view heap's descriptor handle increment, in bytes.</summary>
    public uint ViewIncrement { get; private set; }
    /// <summary>Gets the sampler heap's descriptor handle increment, in bytes.</summary>
    public uint SamplerIncrement { get; private set; }
    /// <summary>Gets the native <c>ID3D12DescriptorHeap</c> the device's pools are ranges of, or zero once
    /// disposed.</summary>
    public nint ViewHeap => m_viewHeap;
    /// <summary>Gets the native shader-visible sampler <c>ID3D12DescriptorHeap</c>, or zero once disposed.</summary>
    public nint SamplerHeap => m_samplerHeap;
    /// <summary>Gets the bytes the view heap occupies, the figure its memory entry records.</summary>
    public ulong ViewHeapBytes => GpuDescriptorHeapBudget.HeapBytes(
        descriptors: m_budget.ViewDescriptors,
        incrementBytes: ViewIncrement
    );
    /// <summary>Gets the bytes the sampler heap occupies, the figure its memory entry records.</summary>
    public ulong SamplerHeapBytes => GpuDescriptorHeapBudget.HeapBytes(
        descriptors: m_budget.SamplerDescriptors,
        incrementBytes: SamplerIncrement
    );

    private nuint ViewCpuStart { get; set; }
    private ulong ViewGpuStart { get; set; }
    private nuint SamplerCpuStart { get; set; }
    private ulong SamplerGpuStart { get; set; }
    private nuint ClearCpuStart { get; set; }

    /// <summary>Creates a device's heaps at the sizes its capabilities report, and counts both shader-visible heaps.</summary>
    /// <param name="device">The device; it outlives the heaps.</param>
    /// <param name="capabilities">The device's capability report, read when it was created.</param>
    /// <param name="memory">The backend's memory counts, or <see langword="null"/> to count nothing.</param>
    /// <param name="shaderVisibleHeapsCreated">The device's count of shader-visible heaps created, raised by one for each
    /// of the two heaps.</param>
    /// <returns>The heaps, owned by the caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="capabilities"/> reports no shader-visible heap.</exception>
    public static DirectXShaderVisibleHeaps Create(ID3D12Device* device, GpuDeviceCapabilities capabilities, GpuDeviceMemoryWork? memory, ref long shaderVisibleHeapsCreated) {
        var heaps = new DirectXShaderVisibleHeaps(
            budget: new GpuDescriptorHeapBudget(capabilities: capabilities),
            device: ((nint)device),
            memory: memory
        );

        try {
            heaps.m_viewHeap = ((nint)DirectXDescriptorHeaps.CreateShaderVisible(
                count: heaps.m_budget.ViewDescriptors,
                created: ref shaderVisibleHeapsCreated,
                device: device,
                type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
            ));
            heaps.ViewIncrement = device->GetDescriptorHandleIncrementSize(DescriptorHeapType: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
            heaps.ViewCpuStart = DirectXConstants.GetCpuHeapStart(heap: ((ID3D12DescriptorHeap*)heaps.m_viewHeap)).ptr;
            heaps.ViewGpuStart = DirectXConstants.GetGpuHeapStart(heap: ((ID3D12DescriptorHeap*)heaps.m_viewHeap)).ptr;
            heaps.Count(
                bytes: heaps.ViewHeapBytes,
                heap: heaps.m_viewHeap
            );
            heaps.m_samplerHeap = ((nint)DirectXDescriptorHeaps.CreateShaderVisible(
                count: heaps.m_budget.SamplerDescriptors,
                created: ref shaderVisibleHeapsCreated,
                device: device,
                type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER
            ));
            heaps.SamplerIncrement = device->GetDescriptorHandleIncrementSize(DescriptorHeapType: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER);
            heaps.SamplerCpuStart = DirectXConstants.GetCpuHeapStart(heap: ((ID3D12DescriptorHeap*)heaps.m_samplerHeap)).ptr;
            heaps.SamplerGpuStart = DirectXConstants.GetGpuHeapStart(heap: ((ID3D12DescriptorHeap*)heaps.m_samplerHeap)).ptr;
            heaps.Count(
                bytes: heaps.SamplerHeapBytes,
                heap: heaps.m_samplerHeap
            );
            heaps.m_clearHeap = ((nint)DirectXDescriptorHeaps.Create(
                count: ClearDescriptors,
                device: device,
                type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
            ));
            heaps.ClearCpuStart = DirectXConstants.GetCpuHeapStart(heap: ((ID3D12DescriptorHeap*)heaps.m_clearHeap)).ptr;
        } catch {
            heaps.Dispose();
            throw;
        }

        return heaps;
    }

    private void Count(nint heap, ulong bytes) =>
        _ = m_memory?.CountAllocated(
            allocation: heap,
            bytes: checked((long)bytes),
            device: m_device,
            role: GpuMemoryRole.DeviceLocal
        );

    /// <summary>Binds both heaps to a command list, once per recording: a reset clears a command list's heaps.</summary>
    /// <param name="commandList">The command list being recorded.</param>
    /// <exception cref="ObjectDisposedException">The heaps were released.</exception>
    public void Bind(ID3D12GraphicsCommandList* commandList) {
        var heaps = stackalloc ID3D12DescriptorHeap*[2];

        heaps[0] = ((ID3D12DescriptorHeap*)m_viewHeap);
        heaps[1] = ((ID3D12DescriptorHeap*)m_samplerHeap);
        ObjectDisposedException.ThrowIf(
            condition: ((null == heaps[0]) || (null == heaps[1])),
            instance: this
        );
        commandList->SetDescriptorHeaps(
            NumDescriptorHeaps: 2,
            ppDescriptorHeaps: heaps
        );
    }
    /// <summary>Checks whether a candidate's pools fit the view heap now, allocating nothing.</summary>
    /// <param name="owner">The candidate's name, echoed in a refusal.</param>
    /// <param name="pools">The pools the candidate would create.</param>
    /// <param name="refusal">Why the candidate does not fit, or empty when it does.</param>
    /// <returns>Whether the candidate fits.</returns>
    public bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) {
        lock (m_gate) {
            return m_budget.CanAdmit(
                owner: owner,
                pools: pools,
                refusal: out refusal
            );
        }
    }
    /// <summary>Admits one pool as a range of the view heap and a range of the sampler heap.</summary>
    /// <param name="sizes">The pool's sizes; its <see cref="GpuDescriptorPoolSizes.HeapDescriptors"/> is the view range's
    /// length and its <see cref="GpuDescriptorPoolSizes.SamplerHeapDescriptors"/> the sampler range's, and a pool of no
    /// view or no sampler takes no range of that heap.</param>
    /// <returns>The pool, whose sets <c>AllocateSet</c> places inside its range.</returns>
    /// <exception cref="InvalidOperationException">No free range holds the pool, or
    /// <see cref="GpuDescriptorHeapBudget.MaxLivePools"/> pools are live; the message carries
    /// <see cref="GpuDescriptorHeapBudget.RefusalCode"/>.</exception>
    public DirectXDescriptorPool AllocatePool(in GpuDescriptorPoolSizes sizes) {
        GpuDescriptorAdmission? admission;
        string refusal;

        lock (m_gate) {
            if (!m_budget.TryAdmit(
                admission: out admission,
                owner: "descriptor pool",
                pools: [sizes],
                refusal: out refusal
            )) {
                throw new GpuDescriptorHeapRefusalException(message: refusal);
            }
        }

        var start = ((admission.Ranges.Count == 0)
            ? 0U
            : admission.Ranges[0].Start
        );
        var samplerStart = ((admission.SamplerRanges.Count == 0)
            ? 0U
            : admission.SamplerRanges[0].Start
        );

        return new DirectXDescriptorPool {
            Admission = admission,
            Capacity = sizes.HeapDescriptors,
            CpuBase = (ViewCpuStart + (((nuint)start) * ViewIncrement)),
            DescriptorSize = ViewIncrement,
            GpuBase = (ViewGpuStart + (((ulong)start) * ViewIncrement)),
            Heaps = this,
            SamplerCapacity = sizes.SamplerHeapDescriptors,
            SamplerCpuBase = (SamplerCpuStart + (((nuint)samplerStart) * SamplerIncrement)),
            SamplerDescriptorSize = SamplerIncrement,
            SamplerGpuBase = (SamplerGpuStart + (((ulong)samplerStart) * SamplerIncrement)),
        };
    }
    /// <summary>Returns a pool's range to the view heap. A pool of heaps already released returns nothing, since its
    /// device is gone.</summary>
    /// <param name="pool">The pool <see cref="AllocatePool"/> returned.</param>
    public void ReleasePool(DirectXDescriptorPool pool) {
        ArgumentNullException.ThrowIfNull(argument: pool);

        lock (m_gate) {
            if (pool.Admission is { } admission) {
                pool.Admission = null;
                m_budget.Release(admission: admission);
            }
        }
    }
    /// <summary>Returns a resource's clear slot for a view: a descriptor in the view heap for a clear's GPU handle and its
    /// mirror in the CPU-only heap for its CPU handle. The first clear to ask that view of the resource takes the slot,
    /// and the caller writes the view into both descriptors then; the slot stays the resource's until it is released
    /// (<see cref="ForgetClears"/>).</summary>
    /// <param name="resource">The <c>ID3D12Resource*</c> the clear writes.</param>
    /// <param name="view">The view the clear names.</param>
    /// <param name="created">Whether the slot is new, its descriptors not yet written.</param>
    /// <returns>The slot.</returns>
    /// <exception cref="GpuRangeExhaustedException"><see cref="ClearDescriptors"/> clear views are held by live
    /// resources.</exception>
    /// <exception cref="ObjectDisposedException">The heaps were released.</exception>
    /// <exception cref="COMException">The resource refused its clear tag.</exception>
    public DirectXClearDescriptor ClearOf(nint resource, in D3D12_UNORDERED_ACCESS_VIEW_DESC view, out bool created) {
        var calls = new DirectXDeviceCommandCalls(device: null);

        if (!calls.TryReadTag(key: ClearKey, resource: ((ID3D12Resource*)resource), value: out var tag)) {
            tag = Interlocked.Increment(location: ref ClearTags);
            calls.Tag(key: ClearKey, resource: ((ID3D12Resource*)resource), value: tag).ThrowOnFailure();
        }

        var clears = Cleared.GetOrAdd(key: tag, valueFactory: static _ => []);
        var asked = MemoryMarshal.AsBytes(span: new ReadOnlySpan<D3D12_UNORDERED_ACCESS_VIEW_DESC>(reference: in view));

        lock (clears) {
            foreach (var clear in clears) {
                if (
                    ReferenceEquals(objA: clear.Heaps, objB: this) &&
                    MemoryMarshal.AsBytes(span: new ReadOnlySpan<D3D12_UNORDERED_ACCESS_VIEW_DESC>(reference: in clear.View)).SequenceEqual(other: asked)
                ) {
                    created = false;

                    return clear.Descriptor;
                }
            }

            var descriptor = AllocateClear();

            clears.Add(item: (this, view, descriptor));
            created = true;

            return descriptor;
        }
    }
    /// <summary>Returns the clear slots a resource holds, as it is released. Does nothing for a resource no clear
    /// wrote.</summary>
    /// <param name="resource">The <c>ID3D12Resource*</c>, still alive.</param>
    public static void ForgetClears(nint resource) {
        if (
            Cleared.IsEmpty ||
            !new DirectXDeviceCommandCalls(device: null).TryReadTag(key: ClearKey, resource: ((ID3D12Resource*)resource), value: out var tag) ||
            !Cleared.TryRemove(key: tag, value: out var clears)
        ) {
            return;
        }

        lock (clears) {
            foreach (var clear in clears) { clear.Descriptor.Dispose(); }
        }
    }

    private DirectXClearDescriptor AllocateClear() {
        uint slot;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(
                condition: (0 == m_viewHeap),
                instance: this
            );
            slot = m_clears.Allocate(count: 1U);
        }

        var heapSlot = (m_clearAdmission.Ranges[0].Start + slot);

        return new DirectXClearDescriptor(
            clearCpu: new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = (ClearCpuStart + (((nuint)slot) * ViewIncrement)), },
            gpu: new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = (ViewGpuStart + (((ulong)heapSlot) * ViewIncrement)), },
            heaps: this,
            slot: slot,
            viewCpu: new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = (ViewCpuStart + (((nuint)heapSlot) * ViewIncrement)), }
        );
    }

    internal void ReleaseClear(uint slot) {
        lock (m_gate) {
            _ = m_clears.Free(start: slot);
        }
    }

    /// <summary>Ends both shader-visible heaps' memory entries and releases the three heaps. Pools and clear slots
    /// still held afterwards return nothing. Safe to call more than once.</summary>
    public void Dispose() {
        lock (m_gate) {
            foreach (var heap in ((ReadOnlySpan<nint>)[m_viewHeap, m_samplerHeap])) {
                if (0 != heap) {
                    _ = m_memory?.CountReleased(
                        allocation: heap,
                        device: m_device
                    );
                }
            }

            DirectXConstants.Release(pointer: ref m_viewHeap);
            DirectXConstants.Release(pointer: ref m_samplerHeap);
            DirectXConstants.Release(pointer: ref m_clearHeap);
        }
    }
}
/// <summary>
/// One storage clear's descriptors from a device's <see cref="DirectXShaderVisibleHeaps"/>: the view-heap slot whose GPU
/// handle the clear names, with its CPU handle for writing the view, and the CPU-only mirror whose CPU handle the clear
/// names. Disposing it returns the slot; the resource it clears holds it until the resource is released.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXClearDescriptor : IDisposable {
    private DirectXShaderVisibleHeaps? m_heaps;

    internal DirectXClearDescriptor(DirectXShaderVisibleHeaps heaps, uint slot, D3D12_CPU_DESCRIPTOR_HANDLE viewCpu, D3D12_GPU_DESCRIPTOR_HANDLE gpu, D3D12_CPU_DESCRIPTOR_HANDLE clearCpu) {
        m_heaps = heaps;
        ClearCpuHandle = clearCpu;
        GpuHandle = gpu;
        Slot = slot;
        ViewCpuHandle = viewCpu;
    }

    /// <summary>Gets the CPU-only heap's handle, the clear's CPU handle.</summary>
    public D3D12_CPU_DESCRIPTOR_HANDLE ClearCpuHandle { get; }
    /// <summary>Gets the view heap's GPU handle, the clear's GPU handle.</summary>
    public D3D12_GPU_DESCRIPTOR_HANDLE GpuHandle { get; }
    /// <summary>Gets the slot's index among the device's <see cref="DirectXShaderVisibleHeaps.ClearDescriptors"/>.</summary>
    public uint Slot { get; }
    /// <summary>Gets the view heap slot's CPU handle, where the clear's view is written for the GPU handle.</summary>
    public D3D12_CPU_DESCRIPTOR_HANDLE ViewCpuHandle { get; }

    /// <summary>Returns the slot. Safe to call more than once.</summary>
    public void Dispose() {
        var heaps = Interlocked.Exchange(
            location1: ref m_heaps,
            value: null
        );

        heaps?.ReleaseClear(slot: Slot);
    }
}
