using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuBindings"/> for Direct3D 12 using shader-visible CBV_SRV_UAV descriptor
/// heaps. Each <see cref="CreatePool"/> call allocates one heap; <see cref="AllocateSet"/> bump-allocates a
/// region of that heap to each set (advancing a per-pool cursor by the layout's slot count and bounds-checking
/// against the heap capacity), so multiple independent sets can share one pool like a Vulkan descriptor pool.
/// Samplers are static in D3D12 root signatures, so <see cref="CreateSampler"/> returns a non-zero sentinel
/// and <see cref="DestroySampler"/> is a no-op.
/// </summary>
/// <param name="deviceContext">The device context whose current device creates every heap and view.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuBindings(DirectXDeviceContext deviceContext) : IGpuBindings {
    private const nint SamplerSentinel = 1;

    /// <inheritdoc/>
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) {
        var pool = ((DirectXDescriptorPool)GCHandle.FromIntPtr(value: poolHandle).Target!);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: descriptorSetLayoutHandle).Target!);
        // Bump-allocate this set's own region from the pool's single shader-visible heap so multiple independent
        // sets can share one pool (matching a Vulkan pool) instead of every set aliasing the whole heap. The first
        // set lands at offset 0, so single-set-per-pool callers are unaffected.
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
            HeapHandle = pool.HeapHandle,
            SlotByBinding = layout.SlotByBinding,
        };

        return GCHandle.ToIntPtr(value: GCHandle.Alloc(value: set));
    }
    /// <inheritdoc/>
    public nint CreatePool(in GpuDescriptorPoolSizes sizes) {
        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var totalDescriptors = ((sizes.CombinedImageSamplerCount + sizes.StorageBufferCount) + sizes.StorageImageCount);
        var capacity = ((totalDescriptors > 0)
            ? totalDescriptors
            : 1
        );
        var heapPtr = DirectXDescriptorHeaps.Create(
            count: capacity,
            device: device,
            shaderVisible: true,
            type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
        );
        var descriptorSize = device->GetDescriptorHandleIncrementSize(DescriptorHeapType: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        var pool = new DirectXDescriptorPool {
            HeapHandle = ((nint)heapPtr),
            DescriptorSize = descriptorSize,
            Capacity = capacity,
            CpuBase = GetCpuHeapStart(heap: heapPtr).ptr,
            GpuBase = GetGpuHeapStart(heap: heapPtr).ptr,
        };

        return GCHandle.ToIntPtr(value: GCHandle.Alloc(value: pool));
    }
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

        if (0 != pool.HeapHandle) {
            _ = ((IUnknown*)pool.HeapHandle)->Release();
            pool.HeapHandle = 0;
        }

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
