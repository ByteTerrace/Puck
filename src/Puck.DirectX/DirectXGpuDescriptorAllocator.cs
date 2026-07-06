using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuDescriptorAllocator"/> for Direct3D 12 using shader-visible CBV_SRV_UAV descriptor
/// heaps. Each <see cref="CreatePool"/> call allocates one heap; <see cref="AllocateSet"/> bump-allocates a
/// region of that heap to each set (advancing a per-pool cursor by the layout's slot count and bounds-checking
/// against the heap capacity), so multiple independent sets can share one pool like a Vulkan descriptor pool.
/// Samplers are static in D3D12 root signatures, so <see cref="CreateSampler"/> returns a non-zero sentinel
/// and <see cref="DestroySampler"/> is a no-op.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuDescriptorAllocator : IGpuDescriptorAllocator {
    private const nint SamplerSentinel = 1;

    /// <inheritdoc/>
    public nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) {
        var pool = (DirectXDescriptorPool)GCHandle.FromIntPtr(poolHandle).Target!;
        var layout = (DirectXPipelineLayout)GCHandle.FromIntPtr(descriptorSetLayoutHandle).Target!;
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
            HeapHandle = pool.HeapHandle,
            DescriptorSize = pool.DescriptorSize,
            CpuBase = (pool.CpuBase + ((nuint)offset * pool.DescriptorSize)),
            GpuBase = (pool.GpuBase + ((ulong)offset * pool.DescriptorSize)),
            SlotByBinding = layout.SlotByBinding,
        };

        return GCHandle.ToIntPtr(GCHandle.Alloc(set));
    }

    /// <inheritdoc/>
    public nint CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) {
        var device = (ID3D12Device*)deviceHandle;
        // An acceleration-structure SRV is just another CBV_SRV_UAV heap slot, so it adds to the total like the rest.
        var totalDescriptors = (sizes.CombinedImageSamplerCount + sizes.StorageBufferCount + sizes.StorageImageCount + sizes.AccelerationStructureCount);
        var heapDesc = new D3D12_DESCRIPTOR_HEAP_DESC {
            Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
            NumDescriptors = (totalDescriptors > 0) ? totalDescriptors : 1,
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
        };

        device->CreateDescriptorHeap(
            pDescriptorHeapDesc: in heapDesc,
            riid: ID3D12DescriptorHeap.IID_Guid,
            ppvHeap: out var heap
        );

        var heapPtr = (ID3D12DescriptorHeap*)heap;
        var descriptorSize = device->GetDescriptorHandleIncrementSize(
            DescriptorHeapType: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
        );
        var pool = new DirectXDescriptorPool {
            HeapHandle = (nint)heap,
            DescriptorSize = descriptorSize,
            Capacity = heapDesc.NumDescriptors,
            CpuBase = GetCpuHeapStart(heapPtr).ptr,
            GpuBase = GetGpuHeapStart(heapPtr).ptr,
        };

        return GCHandle.ToIntPtr(GCHandle.Alloc(pool));
    }

    /// <inheritdoc/>
    // The filter is ignored: Direct3D 12 samplers are static in the root signature, so the filter is baked into the
    // compute pipeline's static sampler (via the factory's samplerFilter) rather than carried by this handle.
    public nint CreateSampler(nint deviceHandle, GpuSamplerFilter filter = GpuSamplerFilter.Linear) => SamplerSentinel;

    /// <inheritdoc/>
    public void DestroyPool(nint deviceHandle, nint poolHandle) {
        var gcHandle = GCHandle.FromIntPtr(poolHandle);
        var pool = (DirectXDescriptorPool)gcHandle.Target!;

        if (0 != pool.HeapHandle) {
            _ = ((IUnknown*)pool.HeapHandle)->Release();
            pool.HeapHandle = 0;
        }

        gcHandle.Free();
    }

    /// <inheritdoc/>
    public void DestroySampler(nint deviceHandle, nint samplerHandle) { }

    /// <inheritdoc/>
    public void WriteCombinedImageSampler(
        nint deviceHandle,
        nint descriptorSetHandle,
        uint binding,
        uint arrayElement,
        nint imageViewHandle,
        nint samplerHandle
    ) {
        var device = (ID3D12Device*)deviceHandle;
        var set = (DirectXDescriptorSet)GCHandle.FromIntPtr(descriptorSetHandle).Target!;
        var imageView = (DirectXImageView)GCHandle.FromIntPtr(imageViewHandle).Target!;
        var slotIndex = (set.SlotByBinding[binding] + arrayElement);
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = set.CpuBase + ((nuint)(slotIndex * set.DescriptorSize)),
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
            pResource: (ID3D12Resource*)imageView.ResourceHandle,
            pDesc: &srvDesc,
            DestDescriptor: cpuHandle
        );
    }

    /// <summary>Writes a top-level acceleration structure (TLAS) SRV into a set, identifying it by its GPU virtual
    /// address. On Direct3D 12 the TLAS binds as a <c>RaytracingAccelerationStructure</c> SRV, created with a null
    /// resource and the AS's GPU VA (passed as the neutral backend-defined reference).</summary>
    /// <param name="deviceHandle">The native <c>ID3D12Device</c> handle.</param>
    /// <param name="descriptorSetHandle">The descriptor-set token to write into.</param>
    /// <param name="binding">The binding index within the set (its heap slot).</param>
    /// <param name="accelerationStructureReference">The TLAS GPU virtual address (the Direct3D 12 backend reference).</param>
    public void WriteAccelerationStructure(
        nint deviceHandle,
        nint descriptorSetHandle,
        uint binding,
        nint accelerationStructureReference
    ) {
        // The acceleration-structure SRV path is DXR (Windows 10 1809+); a caller only reaches it after the
        // acceleration structure reported supported, which implies that OS, so an older OS is a no-op.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) {
            return;
        }

        var device = (ID3D12Device*)deviceHandle;
        var set = (DirectXDescriptorSet)GCHandle.FromIntPtr(descriptorSetHandle).Target!;
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = set.CpuBase + ((nuint)(set.SlotByBinding[binding] * set.DescriptorSize)),
        };
        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_RAYTRACING_ACCELERATION_STRUCTURE,
        };

        srvDesc.Anonymous.RaytracingAccelerationStructure = new D3D12_RAYTRACING_ACCELERATION_STRUCTURE_SRV {
            Location = (ulong)accelerationStructureReference,
        };

        // An acceleration-structure SRV is identified solely by its GPU VA — the resource argument MUST be null.
        device->CreateShaderResourceView(
            pResource: (ID3D12Resource*)null,
            pDesc: &srvDesc,
            DestDescriptor: cpuHandle
        );
    }

    /// <inheritdoc/>
    public void WriteStorageBuffer(
        nint deviceHandle,
        nint descriptorSetHandle,
        uint binding,
        nint bufferHandle,
        ulong bufferSize
    ) {
        var device = (ID3D12Device*)deviceHandle;
        var set = (DirectXDescriptorSet)GCHandle.FromIntPtr(descriptorSetHandle).Target!;
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = set.CpuBase + ((nuint)(set.SlotByBinding[binding] * set.DescriptorSize)),
        };
        // A read-only StructuredBuffer<uint4> SRV (stride 16) — matches the HLSL and is valid on the upload-heap
        // buffer, where a UAV is not. Each element is one uint4 program word, so NumElements = size / 16.
        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_BUFFER,
        };

        srvDesc.Anonymous.Buffer = new D3D12_BUFFER_SRV {
            FirstElement = 0,
            Flags = D3D12_BUFFER_SRV_FLAGS.D3D12_BUFFER_SRV_FLAG_NONE,
            NumElements = (uint)(bufferSize / 16),
            StructureByteStride = 16,
        };

        device->CreateShaderResourceView(
            pResource: (ID3D12Resource*)bufferHandle,
            pDesc: &srvDesc,
            DestDescriptor: cpuHandle
        );
    }

    /// <inheritdoc/>
    public void WriteStorageBufferReadOnly(
        nint deviceHandle,
        nint descriptorSetHandle,
        uint binding,
        nint bufferHandle,
        ulong bufferSize
    ) {
        var device = (ID3D12Device*)deviceHandle;
        var set = (DirectXDescriptorSet)GCHandle.FromIntPtr(descriptorSetHandle).Target!;
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = set.CpuBase + ((nuint)(set.SlotByBinding[binding] * set.DescriptorSize)),
        };
        // A read-only StructuredBuffer<uint>/<float> SRV with the matching 4-byte element stride — for a default-heap
        // buffer a compute pass wrote (the cull buffer, the cull-args bbox bounds). The stride-16 WriteStorageBuffer
        // SRV is specific to the uint4 program-word buffer; using it for a small 4-byte-element buffer yields a wrong
        // (and, for an 8-byte buffer, zero-element) view that the shader's indexed read page-faults on.
        var srvDesc = new D3D12_SHADER_RESOURCE_VIEW_DESC {
            Format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_BUFFER,
        };

        srvDesc.Anonymous.Buffer = new D3D12_BUFFER_SRV {
            FirstElement = 0,
            Flags = D3D12_BUFFER_SRV_FLAGS.D3D12_BUFFER_SRV_FLAG_NONE,
            NumElements = (uint)(bufferSize / sizeof(uint)),
            StructureByteStride = sizeof(uint),
        };

        device->CreateShaderResourceView(
            pResource: (ID3D12Resource*)bufferHandle,
            pDesc: &srvDesc,
            DestDescriptor: cpuHandle
        );
    }

    /// <inheritdoc/>
    public void WriteStorageBufferReadWrite(
        nint deviceHandle,
        nint descriptorSetHandle,
        uint binding,
        nint bufferHandle,
        ulong bufferSize
    ) {
        var device = (ID3D12Device*)deviceHandle;
        var set = (DirectXDescriptorSet)GCHandle.FromIntPtr(descriptorSetHandle).Target!;
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = set.CpuBase + ((nuint)(set.SlotByBinding[binding] * set.DescriptorSize)),
        };
        // A RWStructuredBuffer<float> UAV (stride 4) over the default-heap cull buffer the beam prepass writes.
        var uavDesc = new D3D12_UNORDERED_ACCESS_VIEW_DESC {
            Format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_BUFFER,
        };

        uavDesc.Anonymous.Buffer = new D3D12_BUFFER_UAV {
            CounterOffsetInBytes = 0,
            FirstElement = 0,
            Flags = D3D12_BUFFER_UAV_FLAGS.D3D12_BUFFER_UAV_FLAG_NONE,
            NumElements = (uint)(bufferSize / sizeof(float)),
            StructureByteStride = sizeof(float),
        };

        device->CreateUnorderedAccessView(
            pResource: (ID3D12Resource*)bufferHandle,
            pCounterResource: (ID3D12Resource*)null,
            pDesc: &uavDesc,
            DestDescriptor: cpuHandle
        );
    }

    /// <inheritdoc/>
    public void WriteStorageImage(
        nint deviceHandle,
        nint descriptorSetHandle,
        uint binding,
        uint arrayElement,
        nint imageViewHandle
    ) {
        var device = (ID3D12Device*)deviceHandle;
        var set = (DirectXDescriptorSet)GCHandle.FromIntPtr(descriptorSetHandle).Target!;
        var imageView = (DirectXImageView)GCHandle.FromIntPtr(imageViewHandle).Target!;
        var slotIndex = (set.SlotByBinding[binding] + arrayElement);
        var cpuHandle = new D3D12_CPU_DESCRIPTOR_HANDLE {
            ptr = set.CpuBase + ((nuint)(slotIndex * set.DescriptorSize)),
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
            pResource: (ID3D12Resource*)imageView.ResourceHandle,
            pCounterResource: null,
            pDesc: &uavDesc,
            DestDescriptor: cpuHandle
        );
    }

}
