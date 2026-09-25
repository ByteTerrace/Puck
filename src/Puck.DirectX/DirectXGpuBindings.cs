using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuBindings"/> for Direct3D 12 over the device's two shader-visible descriptor heaps
/// (<see cref="DirectXShaderVisibleHeaps"/>), which it creates when its device context brings a device up
/// (<see cref="CreateDeviceHeaps"/>) and releases when the context releases the device (<see cref="ReleaseDeviceHeaps"/>),
/// so a recreated device gets a fresh pair. Each <see cref="CreatePool"/> admits one range of the view heap, and one of
/// the sampler heap for a pool holding samplers, through the device's <see cref="GpuDescriptorHeapBudget"/> and
/// <see cref="DestroyPool"/> returns them; <see cref="AllocateSet"/> bump-allocates each set's region inside its pool's
/// ranges (advancing a per-pool cursor by the layout's slot count and bounds-checking against the range), so several
/// sets share one pool like a Vulkan descriptor pool. A pipeline created without a layout description reads its samplers
/// as static samplers in its root signature, so <see cref="CreateSampler"/> returns a non-zero sentinel and
/// <see cref="DestroySampler"/> is a no-op.
/// </summary>
/// <param name="deviceContext">The device context whose current device creates every heap and view.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuBindings(DirectXDeviceContext deviceContext) : IGpuBindings {
    private const nint SamplerSentinel = 1;

    private DirectXShaderVisibleHeaps? m_heaps;

    /// <summary>Gets the current device's shader-visible heaps, creating the device first when it does not exist yet.</summary>
    /// <exception cref="GpuDeviceUnavailableException">No device could be created.</exception>
    public DirectXShaderVisibleHeaps Heaps {
        get {
            _ = deviceContext.Device;

            return (m_heaps ?? throw new InvalidOperationException(message: "The Direct3D 12 device was brought up without its shader-visible descriptor heaps."));
        }
    }

    /// <summary>Creates the shader-visible heaps of a device just brought up, at the sizes its capabilities report.
    /// Its context calls it once per device, before any pool is created on it.</summary>
    /// <param name="device">The device.</param>
    /// <param name="capabilities">The device's capability report.</param>
    /// <exception cref="InvalidOperationException">The previous device's heaps were not released.</exception>
    public void CreateDeviceHeaps(ID3D12Device* device, GpuDeviceCapabilities capabilities) {
        if (m_heaps is not null) {
            throw new InvalidOperationException(message: "The previous device's shader-visible descriptor heaps are still held.");
        }

        m_heaps = DirectXShaderVisibleHeaps.Create(
            capabilities: capabilities,
            device: device,
            memory: deviceContext.Memory
        );
    }
    /// <summary>Releases the current device's heaps and ends their memory entries, before the device itself is
    /// released. Pools still held afterwards return nothing when destroyed. Does nothing when no heaps are held.</summary>
    public void ReleaseDeviceHeaps() {
        var heaps = m_heaps;

        m_heaps = null;
        heaps?.Dispose();
    }
    /// <inheritdoc/>
    /// <remarks>A group's layout handle (<see cref="IGpuComputePipeline.GroupLayoutHandles"/>) takes a region of the
    /// pool's view range as long as the group's view table and a region of its sampler range as long as its sampler
    /// table, so a group's samplers live in the device's sampler heap.</remarks>
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) {
        var pool = ((DirectXDescriptorPool)GCHandle.FromIntPtr(value: poolHandle).Target!);

        if (GCHandle.FromIntPtr(value: descriptorSetLayoutHandle).Target is DirectXGroupLayout group) {
            return AllocateGroupSet(
                group: group,
                pool: pool
            );
        }

        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: descriptorSetLayoutHandle).Target!);
        var offset = pool.NextOffset;
        var slotCount = layout.DescriptorSlotCount;

        if ((offset + slotCount) > pool.Capacity) {
            throw new InvalidOperationException(message: $"The descriptor pool (capacity {pool.Capacity}) cannot fit another {slotCount}-slot set at offset {offset}.");
        }

        pool.NextOffset = (offset + slotCount);

        var set = new DirectXDescriptorSet {
            CpuBase = (pool.CpuBase + (((nuint)offset) * pool.DescriptorSize)),
            DescriptorSize = pool.DescriptorSize,
            GpuBase = (pool.GpuBase + (((ulong)offset) * pool.DescriptorSize)),
            SlotByBinding = layout.SlotByBinding,
        };

        return GCHandle.ToIntPtr(value: GCHandle.Alloc(value: set));
    }

    private static nint AllocateGroupSet(DirectXDescriptorPool pool, DirectXGroupLayout group) {
        var offset = pool.NextOffset;
        var samplerOffset = pool.SamplerNextOffset;

        if (
            ((offset + group.ViewSlotCount) > pool.Capacity) ||
            ((samplerOffset + group.SamplerSlotCount) > pool.SamplerCapacity)
        ) {
            throw new InvalidOperationException(message: $"The descriptor pool (capacity {pool.Capacity} views and {pool.SamplerCapacity} samplers) cannot fit group {group.Ordinal}'s set of {group.ViewSlotCount} views and {group.SamplerSlotCount} samplers at offsets {offset} and {samplerOffset}.");
        }

        pool.NextOffset = (offset + group.ViewSlotCount);
        pool.SamplerNextOffset = (samplerOffset + group.SamplerSlotCount);

        var set = new DirectXDescriptorSet {
            CpuBase = (pool.CpuBase + (((nuint)offset) * pool.DescriptorSize)),
            DescriptorSize = pool.DescriptorSize,
            GpuBase = (pool.GpuBase + (((ulong)offset) * pool.DescriptorSize)),
            Group = group,
            SamplerCpuBase = (pool.SamplerCpuBase + (((nuint)samplerOffset) * pool.SamplerDescriptorSize)),
            SamplerDescriptorSize = pool.SamplerDescriptorSize,
            SamplerGpuBase = (pool.SamplerGpuBase + (((ulong)samplerOffset) * pool.SamplerDescriptorSize)),
            SlotByBinding = group.SlotByBinding,
        };

        return GCHandle.ToIntPtr(value: GCHandle.Alloc(value: set));
    }

    /// <inheritdoc/>
    public bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) =>
        Heaps.CanAdmit(
            owner: owner,
            pools: pools,
            refusal: out refusal
        );
    /// <inheritdoc/>
    public nint CreatePool(in GpuDescriptorPoolSizes sizes) =>
        GCHandle.ToIntPtr(value: GCHandle.Alloc(value: Heaps.AllocatePool(sizes: in sizes)));
    /// <inheritdoc/>
    // The filter is ignored: Direct3D 12 samplers are static in the root signature, so the filter is baked into the
    // compute pipeline's static sampler (via the factory's samplerFilter) rather than carried by this handle.
    public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) => SamplerSentinel;
    /// <inheritdoc/>
    public void DestroyPool(nint poolHandle) {
        if (0 == poolHandle) {
            return;
        }

        var gcHandle = GCHandle.FromIntPtr(value: poolHandle);
        var pool = ((DirectXDescriptorPool)gcHandle.Target!);

        pool.Heaps?.ReleasePool(pool: pool);
        gcHandle.Free();
    }
    /// <inheritdoc/>
    public void DestroySampler(nint samplerHandle) { }
    /// <inheritdoc/>
    public void WriteCombinedImageSampler(
        nint descriptorSetHandle,
        uint binding,
        uint arrayElement,
        nint imageViewHandle,
        nint samplerHandle
    ) {
        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var set = ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: descriptorSetHandle).Target!);
        var imageView = ((DirectXImageView)GCHandle.FromIntPtr(value: imageViewHandle).Target!);
        var slotIndex = (set.SlotByBinding[binding] + arrayElement);
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = (set.CpuBase + ((nuint)(slotIndex * set.DescriptorSize))),
        };
        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = imageView.Format,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
        };

        srvDesc.Anonymous.Texture2D = new D3D12_TEX2D_SRV {
            MipLevels = 1,
            MostDetailedMip = 0,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };

        device->CreateShaderResourceView(
            DestDescriptor: cpuHandle,
            pDesc: &srvDesc,
            pResource: ((ID3D12Resource*)imageView.ResourceHandle)
        );
    }
    /// <inheritdoc/>
    /// <remarks>A raw view (a zero <paramref name="elementStride"/>) is <c>R32_TYPELESS</c> with the RAW flag over
    /// 4-byte words, the only view a <c>ByteAddressBuffer</c> declaration reads. A structured view has an unknown format
    /// and the declared stride, so its element count is right for a buffer smaller than any other stride would
    /// allow; a larger stride over a small buffer is a zero-element view the shader's indexed read page-faults on. A
    /// read-write view is valid only over a default-heap buffer.</remarks>
    public void WriteBuffer(
        nint descriptorSetHandle,
        uint binding,
        nint bufferHandle,
        ulong bufferSize,
        GpuBindingKind kind,
        uint elementStride
    ) {
        if (kind is not (GpuBindingKind.ReadOnlyBuffer or GpuBindingKind.ReadWriteBuffer)) {
            throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "A buffer write names a read-only or read-write buffer kind.",
                paramName: nameof(kind)
            );
        }

        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var set = ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: descriptorSetHandle).Target!);
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = (set.CpuBase + ((nuint)(set.SlotByBinding[binding] * set.DescriptorSize))),
        };
        var raw = (0 == elementStride);
        var format = (raw
            ? DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS
            : DXGI_FORMAT.DXGI_FORMAT_UNKNOWN
        );
        var elements = ((uint)(bufferSize / (raw
            ? sizeof(uint)
            : elementStride
        )));

        if (kind == GpuBindingKind.ReadWriteBuffer) {
            var uavDesc = new D3D12_UNORDERED_ACCESS_VIEW_DESC {
                Format = format,
                ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_BUFFER,
            };

            uavDesc.Anonymous.Buffer = new D3D12_BUFFER_UAV {
                CounterOffsetInBytes = 0,
                FirstElement = 0,
                Flags = (raw
                    ? D3D12_BUFFER_UAV_FLAGS.D3D12_BUFFER_UAV_FLAG_RAW
                    : D3D12_BUFFER_UAV_FLAGS.D3D12_BUFFER_UAV_FLAG_NONE
                ),
                NumElements = elements,
                StructureByteStride = elementStride,
            };

            device->CreateUnorderedAccessView(
                DestDescriptor: cpuHandle,
                pCounterResource: ((ID3D12Resource*)null),
                pDesc: &uavDesc,
                pResource: ((ID3D12Resource*)bufferHandle)
            );

            return;
        }

        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = format,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_BUFFER,
        };

        srvDesc.Anonymous.Buffer = new D3D12_BUFFER_SRV {
            FirstElement = 0,
            Flags = (raw
                ? D3D12_BUFFER_SRV_FLAGS.D3D12_BUFFER_SRV_FLAG_RAW
                : D3D12_BUFFER_SRV_FLAGS.D3D12_BUFFER_SRV_FLAG_NONE
            ),
            NumElements = elements,
            StructureByteStride = elementStride,
        };

        device->CreateShaderResourceView(
            DestDescriptor: cpuHandle,
            pDesc: &srvDesc,
            pResource: ((ID3D12Resource*)bufferHandle)
        );
    }
    /// <inheritdoc/>
    public void WriteStorageImage(
        nint descriptorSetHandle,
        uint binding,
        uint arrayElement,
        nint imageViewHandle
    ) {
        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var set = ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: descriptorSetHandle).Target!);
        var imageView = ((DirectXImageView)GCHandle.FromIntPtr(value: imageViewHandle).Target!);
        var slotIndex = (set.SlotByBinding[binding] + arrayElement);
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = (set.CpuBase + ((nuint)(slotIndex * set.DescriptorSize))),
        };
        var uavDesc = new D3D12_UNORDERED_ACCESS_VIEW_DESC {
            Format = imageView.Format,
            ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_TEXTURE2D,
        };

        uavDesc.Anonymous.Texture2D = new D3D12_TEX2D_UAV {
            MipSlice = 0,
            PlaneSlice = 0,
        };

        device->CreateUnorderedAccessView(
            DestDescriptor: cpuHandle,
            pCounterResource: null,
            pDesc: &uavDesc,
            pResource: ((ID3D12Resource*)imageView.ResourceHandle)
        );
    }

}
