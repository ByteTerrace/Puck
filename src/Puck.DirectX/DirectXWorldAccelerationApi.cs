using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Builds and records the Direct3D 12 ray-query world acceleration structures (DXR 1.1). The D3D12 peer of
/// <c>VulkanWorldAccelerationApi</c>: a single unit-AABB BLAS shared by every instance, the TLAS rebuilt every
/// frame over a persistently mapped instance buffer. All the scene policy (the unit AABB, the fast-trace BLAS /
/// fast-build TLAS choice, the build barriers, the instance transform/mask layout) lives here. Requires a device
/// with <c>D3D12_RAYTRACING_TIER_1_1</c> (inline ray tracing); gate with <see cref="SupportsDevice"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe class DirectXWorldAccelerationApi {
    private const uint AabbByteSize = 24; // D3D12_RAYTRACING_AABB: 6 floats
    private const uint InstanceByteSize = 64; // D3D12_RAYTRACING_INSTANCE_DESC

    /// <summary>Whether the device supports DXR 1.1 inline ray tracing (the gate for the whole ray-query world path).</summary>
    /// <param name="deviceHandle">The native <c>ID3D12Device</c> handle.</param>
    /// <returns><see langword="true"/> if the device reports <c>D3D12_RAYTRACING_TIER_1_1</c> or higher; otherwise, <see langword="false"/>.</returns>
    public bool SupportsDevice(nint deviceHandle) {
        if (0 == deviceHandle) {
            return false;
        }

        var device = (ID3D12Device*)deviceHandle;
        D3D12_FEATURE_DATA_D3D12_OPTIONS5 options = default;

        // CsWin32's friendly CheckFeatureSupport overload throws on a failing HRESULT; an unsupported feature query
        // (or an older device) means no ray tracing, so treat any failure as unsupported rather than propagating.
        try {
            device->CheckFeatureSupport(
                Feature: D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS5,
                pFeatureSupportData: &options,
                FeatureSupportDataSize: (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS5)
            );
        } catch {
            return false;
        }

        return (options.RaytracingTier >= D3D12_RAYTRACING_TIER.D3D12_RAYTRACING_TIER_1_1);
    }

    /// <summary>Creates the shared unit-AABB BLAS, the per-frame TLAS, their backing and scratch buffers, and the
    /// persistently mapped AABB and instance upload buffers.</summary>
    /// <param name="deviceHandle">The native <c>ID3D12Device</c> handle.</param>
    /// <param name="maxInstanceCount">The instance-buffer / TLAS instance capacity.</param>
    /// <returns>The created acceleration-structure resources.</returns>
    public DirectXWorldAccelerationResources CreateResources(nint deviceHandle, uint maxInstanceCount) {
        if (0 == deviceHandle) {
            throw new ArgumentException(message: "Acceleration-structure resources require a device handle.", paramName: nameof(deviceHandle));
        }

        if (0 == maxInstanceCount) {
            throw new ArgumentOutOfRangeException(actualValue: maxInstanceCount, message: "Acceleration-structure instance capacity must be greater than zero.", paramName: nameof(maxInstanceCount));
        }

        var device = (ID3D12Device*)deviceHandle;
        var device5 = QueryDevice5(device: device);

        try {
            // Unit AABB [-1, 1]^3, written once into a mapped upload buffer.
            var aabbBuffer = CreateBuffer(device: device, sizeBytes: AabbByteSize, heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE, initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
            var aabbValues = (float*)Map(resource: aabbBuffer);

            aabbValues[0] = -1.0f; aabbValues[1] = -1.0f; aabbValues[2] = -1.0f; // MinX, MinY, MinZ
            aabbValues[3] = 1.0f; aabbValues[4] = 1.0f; aabbValues[5] = 1.0f;    // MaxX, MaxY, MaxZ
            ((ID3D12Resource*)aabbBuffer)->Unmap(0, (D3D12_RANGE*)null);

            var aabbAddress = ((ID3D12Resource*)aabbBuffer)->GetGPUVirtualAddress();

            // BLAS sizing: a single procedural-AABB geometry, PREFER_FAST_TRACE (the geometry never changes). The
            // geometry local stays alive across the prebuild call, which reads it through the inputs' pointer.
            D3D12_RAYTRACING_GEOMETRY_DESC blasGeometry;
            var blasInputs = BlasInputs(aabbGpuAddress: aabbAddress, geometry: &blasGeometry);

            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO blasInfo;
            device5->GetRaytracingAccelerationStructurePrebuildInfo(&blasInputs, &blasInfo);

            var blasBuffer = CreateBuffer(device: device, sizeBytes: blasInfo.ResultDataMaxSizeInBytes, heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
            var blasScratch = CreateBuffer(device: device, sizeBytes: blasInfo.ScratchDataSizeInBytes, heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);
            var blasAddress = ((ID3D12Resource*)blasBuffer)->GetGPUVirtualAddress();

            // Instance buffer: a mapped upload buffer the world rewrites; cleared so unused slots are inert.
            var instanceBufferSize = ((ulong)maxInstanceCount * InstanceByteSize);
            var instanceBuffer = CreateBuffer(device: device, sizeBytes: instanceBufferSize, heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE, initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
            var instanceMapped = Map(resource: instanceBuffer);

            new Span<byte>(pointer: (void*)instanceMapped, length: (int)instanceBufferSize).Clear();

            var instanceAddress = ((ID3D12Resource*)instanceBuffer)->GetGPUVirtualAddress();

            // TLAS sizing, PREFER_FAST_BUILD (rebuilt from scratch each frame).
            var tlasInputs = TlasInputs(instanceGpuAddress: instanceAddress, instanceCount: maxInstanceCount);

            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO tlasInfo;
            device5->GetRaytracingAccelerationStructurePrebuildInfo(&tlasInputs, &tlasInfo);

            var tlasBuffer = CreateBuffer(device: device, sizeBytes: tlasInfo.ResultDataMaxSizeInBytes, heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
            var tlasScratch = CreateBuffer(device: device, sizeBytes: tlasInfo.ScratchDataSizeInBytes, heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);
            var tlasAddress = ((ID3D12Resource*)tlasBuffer)->GetGPUVirtualAddress();

            return new DirectXWorldAccelerationResources(
                AabbBufferGpuAddress: aabbAddress,
                AabbBufferHandle: aabbBuffer,
                BlasBufferHandle: blasBuffer,
                BlasGpuAddress: blasAddress,
                BlasScratchBufferHandle: blasScratch,
                InstanceBufferGpuAddress: instanceAddress,
                InstanceBufferHandle: instanceBuffer,
                InstanceBufferMappedPointer: instanceMapped,
                MaxInstanceCount: maxInstanceCount,
                TlasBufferHandle: tlasBuffer,
                TlasGpuAddress: tlasAddress,
                TlasScratchBufferHandle: tlasScratch
            );
        } finally {
            _ = ((IUnknown*)device5)->Release();
        }
    }

    /// <summary>Writes one <c>D3D12_RAYTRACING_INSTANCE_DESC</c> (64 bytes) into the mapped instance buffer: a
    /// per-axis scale plus translation transform over the shared unit-AABB BLAS, with the caller's instance id and
    /// visibility mask. Written as the raw 64-byte layout (transform 3x4, then the packed id/mask and
    /// contribution/flags words, then the BLAS GPU address) to avoid the generated struct's bit-field accessors.</summary>
    /// <param name="instanceBufferMappedPointer">The persistently mapped instance-buffer pointer to write through.</param>
    /// <param name="index">The instance index within the buffer to write.</param>
    /// <param name="scaleX">The instance's half-extent along the world X axis.</param>
    /// <param name="scaleY">The instance's half-extent along the world Y axis.</param>
    /// <param name="scaleZ">The instance's half-extent along the world Z axis.</param>
    /// <param name="worldCenterX">The instance's world-space center X coordinate.</param>
    /// <param name="worldCenterY">The instance's world-space center Y coordinate.</param>
    /// <param name="worldCenterZ">The instance's world-space center Z coordinate.</param>
    /// <param name="instanceId">The 24-bit instance id reported by ray queries that hit this instance.</param>
    /// <param name="visibilityMask">The 8-bit visibility mask gating which rays may intersect this instance.</param>
    /// <param name="blasGpuAddress">The GPU virtual address of the bottom-level structure this instance references.</param>
    public void WriteInstance(
        nint instanceBufferMappedPointer,
        int index,
        float scaleX,
        float scaleY,
        float scaleZ,
        float worldCenterX,
        float worldCenterY,
        float worldCenterZ,
        uint instanceId,
        uint visibilityMask,
        ulong blasGpuAddress
    ) {
        if (0 == instanceBufferMappedPointer) {
            return;
        }

        var entry = ((byte*)instanceBufferMappedPointer + (index * InstanceByteSize));
        var transform = (float*)entry;

        // Row-major 3x4: per-axis scale on the diagonal, translation in the last column.
        transform[0] = scaleX; transform[1] = 0.0f; transform[2] = 0.0f; transform[3] = worldCenterX;
        transform[4] = 0.0f; transform[5] = scaleY; transform[6] = 0.0f; transform[7] = worldCenterY;
        transform[8] = 0.0f; transform[9] = 0.0f; transform[10] = scaleZ; transform[11] = worldCenterZ;
        // InstanceID:24 | InstanceMask:8, then InstanceContributionToHitGroupIndex:24 | Flags:8.
        *(uint*)(entry + 48) = ((instanceId & 0x00FFFFFF) | (visibilityMask << 24));
        *(uint*)(entry + 52) = 0;
        *(ulong*)(entry + 56) = blasGpuAddress;
    }

    /// <summary>Records the per-frame TLAS build (and, when <paramref name="includeBlasBuild"/>, the static unit-AABB
    /// BLAS build) into the command buffer, with UAV barriers ordering the BLAS before the TLAS and the TLAS before
    /// the ray-query dispatch.</summary>
    /// <param name="commandBufferHandle">The Direct3D 12 command-buffer token (decoded to an <c>ID3D12GraphicsCommandList4</c>).</param>
    /// <param name="resources">The acceleration-structure resources to build into.</param>
    /// <param name="instanceCount">The number of leading instance-buffer entries to build the TLAS over.</param>
    /// <param name="includeBlasBuild">Whether to prepend the static unit-AABB BLAS build.</param>
    public void RecordWorldAccelerationBuild(nint commandBufferHandle, in DirectXWorldAccelerationResources resources, uint instanceCount, bool includeBlasBuild) {
        var state = (DirectXCommandBufferState)GCHandle.FromIntPtr(commandBufferHandle).Target!;
        var commandList4 = QueryCommandList4(commandList: (ID3D12GraphicsCommandList*)state.CommandList);

        try {
            if (includeBlasBuild) {
                D3D12_RAYTRACING_GEOMETRY_DESC blasGeometry;
                var blasInputs = BlasInputs(aabbGpuAddress: resources.AabbBufferGpuAddress, geometry: &blasGeometry);
                var blasBuild = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC {
                    DestAccelerationStructureData = resources.BlasGpuAddress,
                    Inputs = blasInputs,
                    ScratchAccelerationStructureData = ((ID3D12Resource*)resources.BlasScratchBufferHandle)->GetGPUVirtualAddress(),
                    SourceAccelerationStructureData = 0,
                };

                commandList4->BuildRaytracingAccelerationStructure(pDesc: &blasBuild, NumPostbuildInfoDescs: 0, pPostbuildInfoDescs: (D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC*)null);
                UavBarrier(commandList: (ID3D12GraphicsCommandList*)state.CommandList, resource: resources.BlasBufferHandle);
            }

            var tlasInputs = TlasInputs(instanceGpuAddress: resources.InstanceBufferGpuAddress, instanceCount: Math.Min(instanceCount, resources.MaxInstanceCount));
            var tlasBuild = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC {
                DestAccelerationStructureData = resources.TlasGpuAddress,
                Inputs = tlasInputs,
                ScratchAccelerationStructureData = ((ID3D12Resource*)resources.TlasScratchBufferHandle)->GetGPUVirtualAddress(),
                SourceAccelerationStructureData = 0,
            };

            commandList4->BuildRaytracingAccelerationStructure(pDesc: &tlasBuild, NumPostbuildInfoDescs: 0, pPostbuildInfoDescs: (D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC*)null);
            UavBarrier(commandList: (ID3D12GraphicsCommandList*)state.CommandList, resource: resources.TlasBufferHandle);
        } finally {
            _ = ((IUnknown*)commandList4)->Release();
        }
    }

    /// <summary>Releases all acceleration-structure buffers.</summary>
    /// <param name="resources">The acceleration-structure resources to release.</param>
    public void DestroyResources(in DirectXWorldAccelerationResources resources) {
        Release(resource: resources.TlasScratchBufferHandle);
        Release(resource: resources.TlasBufferHandle);
        Release(resource: resources.InstanceBufferHandle);
        Release(resource: resources.BlasScratchBufferHandle);
        Release(resource: resources.BlasBufferHandle);
        Release(resource: resources.AabbBufferHandle);
    }

    // Fills the caller-owned geometry struct and returns BLAS build inputs pointing at it. The caller MUST keep the
    // geometry alive (a stack local) across the prebuild/build call that consumes the returned inputs.
    private static D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS BlasInputs(ulong aabbGpuAddress, D3D12_RAYTRACING_GEOMETRY_DESC* geometry) {
        *geometry = new D3D12_RAYTRACING_GEOMETRY_DESC {
            Flags = D3D12_RAYTRACING_GEOMETRY_FLAGS.D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE,
            Type = D3D12_RAYTRACING_GEOMETRY_TYPE.D3D12_RAYTRACING_GEOMETRY_TYPE_PROCEDURAL_PRIMITIVE_AABBS,
        };
        geometry->Anonymous.AABBs = new D3D12_RAYTRACING_GEOMETRY_AABBS_DESC {
            AABBCount = 1,
            AABBs = new D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE {
                StartAddress = aabbGpuAddress,
                StrideInBytes = AabbByteSize,
            },
        };

        var inputs = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS {
            DescsLayout = D3D12_ELEMENTS_LAYOUT.D3D12_ELEMENTS_LAYOUT_ARRAY,
            Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE,
            NumDescs = 1,
            Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL,
        };

        inputs.Anonymous.pGeometryDescs = geometry;

        return inputs;
    }
    private static D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS TlasInputs(ulong instanceGpuAddress, uint instanceCount) {
        var inputs = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS {
            DescsLayout = D3D12_ELEMENTS_LAYOUT.D3D12_ELEMENTS_LAYOUT_ARRAY,
            Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD,
            NumDescs = instanceCount,
            Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL,
        };

        inputs.Anonymous.InstanceDescs = instanceGpuAddress;

        return inputs;
    }
    private static ID3D12Device5* QueryDevice5(ID3D12Device* device) {
        var iid = ID3D12Device5.IID_Guid;

        ((IUnknown*)device)->QueryInterface(in iid, out var device5).ThrowIfFailed(operation: "ID3D12Device->QueryInterface(ID3D12Device5)");

        return (ID3D12Device5*)device5;
    }
    private static ID3D12GraphicsCommandList4* QueryCommandList4(ID3D12GraphicsCommandList* commandList) {
        var iid = ID3D12GraphicsCommandList4.IID_Guid;

        ((IUnknown*)commandList)->QueryInterface(in iid, out var commandList4).ThrowIfFailed(operation: "ID3D12GraphicsCommandList->QueryInterface(ID3D12GraphicsCommandList4)");

        return (ID3D12GraphicsCommandList4*)commandList4;
    }
    private static nint CreateBuffer(ID3D12Device* device, ulong sizeBytes, D3D12_HEAP_TYPE heapType, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES initialState) {
        var heapProperties = new D3D12_HEAP_PROPERTIES { Type = heapType };
        var bufferDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Flags = flags,
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            Height = 1,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = sizeBytes,
        };

        void* buffer;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in bufferDesc,
            initialState,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &buffer
        );

        return (nint)buffer;
    }
    private static nint Map(nint resource) {
        void* mapped;

        ((ID3D12Resource*)resource)->Map(0, (D3D12_RANGE*)null, &mapped);

        return (nint)mapped;
    }
    private static void UavBarrier(ID3D12GraphicsCommandList* commandList, nint resource) {
        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_UAV,
        };

        barrier.Anonymous.UAV = new D3D12_RESOURCE_UAV_BARRIER {
            pResource = (ID3D12Resource*)resource,
        };

        commandList->ResourceBarrier(1, &barrier);
    }
    private static void Release(nint resource) {
        if (0 != resource) {
            _ = ((IUnknown*)resource)->Release();
        }
    }
}
