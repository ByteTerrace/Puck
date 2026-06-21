using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Builds and records the ray-query world acceleration structures on top of the generic
/// <see cref="IVulkanAccelerationStructureApi"/>. This is where the scene model lives: a single unit-AABB BLAS
/// instanced per world primitive, the TLAS rebuilt every frame. Vulkan-only — ray-query has no Direct3D 12 / DXIL
/// counterpart, so there is no neutral-seam equivalent.
/// </summary>
public unsafe sealed class VulkanWorldAccelerationApi(IVulkanAccelerationStructureApi accelerationStructureApi) : IVulkanWorldAccelerationApi {
    // Values verified against the Vulkan SDK 1.4 header (vulkan_core.h).
    private const uint AccelerationStructureTypeTopLevel = 0;
    private const uint AabbByteSize = 24;
    private const uint AccelerationStructureTypeBottomLevel = 1;
    private const uint AccessAccelerationStructureReadBit = 0x00200000;
    private const uint AccessAccelerationStructureWriteBit = 0x00400000;
    private const uint AccessShaderReadBit = 0x00000020;
    private const uint BufferUsageAccelerationStructureBuildInputReadOnlyBit = 0x00080000;
    private const uint BufferUsageAccelerationStructureStorageBit = 0x00100000;
    private const uint BufferUsageShaderDeviceAddressBit = 0x00020000;
    private const uint BufferUsageStorageBufferBit = 0x00000020;
    private const uint BuildAccelerationStructurePreferFastBuildBit = 0x00000008;
    private const uint BuildAccelerationStructurePreferFastTraceBit = 0x00000004;
    private const uint GeometryTypeAabbs = 1;
    private const uint GeometryTypeInstances = 2;
    private const uint InstanceByteSize = 64;
    private const uint PipelineStageAccelerationStructureBuildBit = 0x02000000;
    private const uint PipelineStageComputeShaderBit = 0x00000800;
    private const uint StructureTypeAccelerationStructureGeometryAabbsDataKhr = 1000150003;
    private const uint StructureTypeAccelerationStructureGeometryInstancesDataKhr = 1000150004;
    private const uint StructureTypeAccelerationStructureGeometryKhr = 1000150006;

    /// <inheritdoc/>
    public bool SupportsDevice(nint deviceHandle) {
        return accelerationStructureApi.SupportsDevice(deviceHandle: deviceHandle);
    }
    /// <inheritdoc/>
    public VulkanWorldAccelerationResources CreateResources(VulkanWorldAccelerationCreateRequest request) {
        if (
            (0 == request.InstanceHandle) ||
            (0 == request.PhysicalDeviceHandle) ||
            (0 == request.DeviceHandle)
        ) {
            throw new ArgumentException(
                message: "Acceleration-structure resources require instance, physical-device and device handles.",
                paramName: nameof(request)
            );
        }

        if (0 == request.MaxInstanceCount) {
            throw new ArgumentOutOfRangeException(
                actualValue: request.MaxInstanceCount,
                message: "Acceleration-structure instance capacity must be greater than zero.",
                paramName: nameof(request)
            );
        }

        if (!accelerationStructureApi.SupportsDevice(deviceHandle: request.DeviceHandle)) {
            throw new InvalidOperationException(message: "The device was created without the acceleration-structure command set.");
        }

        var scratchAlignment = accelerationStructureApi.QueryScratchAlignment(
            instanceHandle: request.InstanceHandle,
            physicalDeviceHandle: request.PhysicalDeviceHandle
        );
        var resources = default(VulkanWorldAccelerationResources) with { MaxInstanceCount = request.MaxInstanceCount };

        try {
            // Unit AABB [-1, 1]^3, written once: instance transforms scale it onto each primitive's world bound.
            var (aabbBuffer, aabbMemory) = CreateBuffer(
                hostVisible: true,
                request: request,
                sizeBytes: AabbByteSize,
                usage: BufferUsageAccelerationStructureBuildInputReadOnlyBit | BufferUsageShaderDeviceAddressBit
            );
            resources = resources with { AabbBufferHandle = aabbBuffer, AabbMemoryHandle = aabbMemory };

            var aabbPointer = accelerationStructureApi.MapMemory(
                deviceHandle: request.DeviceHandle,
                memoryHandle: aabbMemory,
                sizeBytes: AabbByteSize
            );
            var aabbValues = (float*)aabbPointer;

            aabbValues[0] = -1.0f;
            aabbValues[1] = -1.0f;
            aabbValues[2] = -1.0f;
            aabbValues[3] = 1.0f;
            aabbValues[4] = 1.0f;
            aabbValues[5] = 1.0f;
            accelerationStructureApi.UnmapMemory(
                deviceHandle: request.DeviceHandle,
                memoryHandle: aabbMemory
            );

            var aabbAddress = accelerationStructureApi.GetBufferDeviceAddress(
                bufferHandle: aabbBuffer,
                deviceHandle: request.DeviceHandle
            );

            resources = resources with { AabbDeviceAddress = aabbAddress };

            // BLAS sizing + storage. PREFER_FAST_TRACE: the BLAS geometry never changes
            // (the per-frame rebuild reproduces the identical single-AABB structure).
            var blasGeometry = CreateAabbGeometry(aabbDeviceAddress: aabbAddress);
            var blasSizes = accelerationStructureApi.GetBuildSizes(
                accelerationStructureType: AccelerationStructureTypeBottomLevel,
                buildFlags: BuildAccelerationStructurePreferFastTraceBit,
                deviceHandle: request.DeviceHandle,
                geometry: in blasGeometry,
                maxPrimitiveCount: 1
            );

            var (blasBuffer, blasMemory) = CreateBuffer(
                hostVisible: false,
                request: request,
                sizeBytes: blasSizes.AccelerationStructureSize,
                usage: BufferUsageAccelerationStructureStorageBit | BufferUsageShaderDeviceAddressBit
            );
            resources = resources with { BlasBufferHandle = blasBuffer, BlasMemoryHandle = blasMemory };

            var blasHandle = accelerationStructureApi.CreateAccelerationStructure(
                accelerationStructureType: AccelerationStructureTypeBottomLevel,
                bufferHandle: blasBuffer,
                deviceHandle: request.DeviceHandle,
                sizeBytes: blasSizes.AccelerationStructureSize
            );

            resources = resources with { BlasHandle = blasHandle };

            var blasAddress = accelerationStructureApi.GetDeviceAddress(
                accelerationStructureHandle: blasHandle,
                deviceHandle: request.DeviceHandle
            );

            resources = resources with { BlasDeviceAddress = blasAddress };

            var (blasScratchBuffer, blasScratchMemory, blasScratchAddress) = CreateScratchBuffer(
                request: request,
                scratchAlignment: scratchAlignment,
                scratchSize: blasSizes.BuildScratchSize
            );
            resources = resources with {
                BlasScratchBufferHandle = blasScratchBuffer,
                BlasScratchMemoryHandle = blasScratchMemory,
                BlasScratchDeviceAddress = blasScratchAddress,
            };

            // Instance buffer: host-visible and persistently mapped — the world rewrites
            // the live entries every frame before the command buffer is submitted
            // (host-coherent writes are visible to the build by submission).
            var instanceBufferSize = ((ulong)request.MaxInstanceCount * InstanceByteSize);

            var (instanceBuffer, instanceMemory) = CreateBuffer(
                hostVisible: true,
                request: request,
                sizeBytes: instanceBufferSize,
                usage: BufferUsageAccelerationStructureBuildInputReadOnlyBit | BufferUsageShaderDeviceAddressBit
            );
            resources = resources with { InstanceBufferHandle = instanceBuffer, InstanceMemoryHandle = instanceMemory };

            var instanceMappedPointer = accelerationStructureApi.MapMemory(
                deviceHandle: request.DeviceHandle,
                memoryHandle: instanceMemory,
                sizeBytes: instanceBufferSize
            );

            new Span<byte>(
                length: (int)instanceBufferSize,
                pointer: (void*)instanceMappedPointer
            ).Clear();
            var instanceAddress = accelerationStructureApi.GetBufferDeviceAddress(
                bufferHandle: instanceBuffer,
                deviceHandle: request.DeviceHandle
            );

            resources = resources with {
                InstanceBufferDeviceAddress = instanceAddress,
                InstanceBufferMappedPointer = instanceMappedPointer,
            };

            // TLAS sizing + storage, PREFER_FAST_BUILD: rebuilt from scratch each frame.
            var tlasGeometry = CreateInstancesGeometry(instanceBufferDeviceAddress: instanceAddress);
            var tlasSizes = accelerationStructureApi.GetBuildSizes(
                accelerationStructureType: AccelerationStructureTypeTopLevel,
                buildFlags: BuildAccelerationStructurePreferFastBuildBit,
                deviceHandle: request.DeviceHandle,
                geometry: in tlasGeometry,
                maxPrimitiveCount: request.MaxInstanceCount
            );

            var (tlasBuffer, tlasMemory) = CreateBuffer(
                hostVisible: false,
                request: request,
                sizeBytes: tlasSizes.AccelerationStructureSize,
                usage: BufferUsageAccelerationStructureStorageBit | BufferUsageShaderDeviceAddressBit
            );
            resources = resources with { TlasBufferHandle = tlasBuffer, TlasMemoryHandle = tlasMemory };

            var tlasHandle = accelerationStructureApi.CreateAccelerationStructure(
                accelerationStructureType: AccelerationStructureTypeTopLevel,
                bufferHandle: tlasBuffer,
                deviceHandle: request.DeviceHandle,
                sizeBytes: tlasSizes.AccelerationStructureSize
            );

            resources = resources with { TlasHandle = tlasHandle };

            var (tlasScratchBuffer, tlasScratchMemory, tlasScratchAddress) = CreateScratchBuffer(
                request: request,
                scratchAlignment: scratchAlignment,
                scratchSize: tlasSizes.BuildScratchSize
            );
            return resources with {
                TlasScratchBufferHandle = tlasScratchBuffer,
                TlasScratchMemoryHandle = tlasScratchMemory,
                TlasScratchDeviceAddress = tlasScratchAddress,
            };
        } catch {
            DestroyResources(
                deviceHandle: request.DeviceHandle,
                resources: resources
            );
            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyResources(nint deviceHandle, VulkanWorldAccelerationResources resources) {
        if (0 == deviceHandle) {
            return;
        }

        accelerationStructureApi.DestroyAccelerationStructure(
            accelerationStructureHandle: resources.TlasHandle,
            deviceHandle: deviceHandle
        );
        accelerationStructureApi.DestroyAccelerationStructure(
            accelerationStructureHandle: resources.BlasHandle,
            deviceHandle: deviceHandle
        );

        if (0 != resources.InstanceBufferMappedPointer) {
            accelerationStructureApi.UnmapMemory(
                deviceHandle: deviceHandle,
                memoryHandle: resources.InstanceMemoryHandle
            );
        }

        accelerationStructureApi.DestroyBuffer(
            bufferHandle: resources.TlasScratchBufferHandle,
            deviceHandle: deviceHandle,
            memoryHandle: resources.TlasScratchMemoryHandle
        );
        accelerationStructureApi.DestroyBuffer(
            bufferHandle: resources.TlasBufferHandle,
            deviceHandle: deviceHandle,
            memoryHandle: resources.TlasMemoryHandle
        );
        accelerationStructureApi.DestroyBuffer(
            bufferHandle: resources.InstanceBufferHandle,
            deviceHandle: deviceHandle,
            memoryHandle: resources.InstanceMemoryHandle
        );
        accelerationStructureApi.DestroyBuffer(
            bufferHandle: resources.BlasScratchBufferHandle,
            deviceHandle: deviceHandle,
            memoryHandle: resources.BlasScratchMemoryHandle
        );
        accelerationStructureApi.DestroyBuffer(
            bufferHandle: resources.BlasBufferHandle,
            deviceHandle: deviceHandle,
            memoryHandle: resources.BlasMemoryHandle
        );
        accelerationStructureApi.DestroyBuffer(
            bufferHandle: resources.AabbBufferHandle,
            deviceHandle: deviceHandle,
            memoryHandle: resources.AabbMemoryHandle
        );
    }
    /// <inheritdoc/>
    public void RecordWorldAccelerationBuild(
        nint deviceHandle,
        nint commandBufferHandle,
        in VulkanWorldAccelerationResources resources,
        uint instanceCount,
        bool includeBlasBuild
    ) {
        if (
            (0 == deviceHandle) ||
            (0 == commandBufferHandle)
        ) {
            throw new ArgumentException(message: "Acceleration-structure build requires device and command-buffer handles.");
        }

        if (
            (0 == resources.BlasHandle) ||
            (0 == resources.TlasHandle)
        ) {
            throw new ArgumentException(
                message: "Acceleration-structure build requires created BLAS/TLAS resources.",
                paramName: nameof(resources)
            );
        }

        if (!accelerationStructureApi.SupportsDevice(deviceHandle: deviceHandle)) {
            throw new InvalidOperationException(message: "The device was created without the acceleration-structure command set.");
        }

        // Previous frame's ray queries (COMPUTE) must retire before this frame's builds
        // overwrite the structures (write-after-read across command buffers chains through
        // queue submission order).
        accelerationStructureApi.CmdMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            destinationAccessMask: AccessAccelerationStructureWriteBit,
            destinationStageMask: PipelineStageAccelerationStructureBuildBit,
            sourceAccessMask: AccessAccelerationStructureReadBit,
            sourceStageMask: PipelineStageComputeShaderBit
        );

        if (includeBlasBuild) {
            // BLAS: the single unit AABB, static for the life of the resources.
            var blasGeometry = CreateAabbGeometry(aabbDeviceAddress: resources.AabbDeviceAddress);

            accelerationStructureApi.CmdBuildAccelerationStructure(
                accelerationStructureType: AccelerationStructureTypeBottomLevel,
                buildFlags: BuildAccelerationStructurePreferFastTraceBit,
                commandBufferHandle: commandBufferHandle,
                destinationAccelerationStructure: resources.BlasHandle,
                deviceHandle: deviceHandle,
                geometry: in blasGeometry,
                primitiveCount: 1,
                scratchDeviceAddress: resources.BlasScratchDeviceAddress
            );

            // The TLAS build consumes the BLAS address it instances. Later recording
            // generations skip this barrier along with the build: their dependency on the
            // retired first-generation build is carried by the re-record fence wait.
            accelerationStructureApi.CmdMemoryBarrier(
                commandBufferHandle: commandBufferHandle,
                deviceHandle: deviceHandle,
                destinationAccessMask: AccessAccelerationStructureReadBit,
                destinationStageMask: PipelineStageAccelerationStructureBuildBit,
                sourceAccessMask: AccessAccelerationStructureWriteBit,
                sourceStageMask: PipelineStageAccelerationStructureBuildBit
            );
        }

        var tlasGeometry = CreateInstancesGeometry(instanceBufferDeviceAddress: resources.InstanceBufferDeviceAddress);

        accelerationStructureApi.CmdBuildAccelerationStructure(
            accelerationStructureType: AccelerationStructureTypeTopLevel,
            buildFlags: BuildAccelerationStructurePreferFastBuildBit,
            commandBufferHandle: commandBufferHandle,
            destinationAccelerationStructure: resources.TlasHandle,
            deviceHandle: deviceHandle,
            geometry: in tlasGeometry,
            primitiveCount: Math.Min(
                val1: instanceCount,
                val2: resources.MaxInstanceCount
            ),
            scratchDeviceAddress: resources.TlasScratchDeviceAddress
        );

        // Publish the fresh TLAS to the compute dispatches that follow.
        accelerationStructureApi.CmdMemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            destinationAccessMask: AccessAccelerationStructureReadBit | AccessShaderReadBit,
            destinationStageMask: PipelineStageComputeShaderBit,
            sourceAccessMask: AccessAccelerationStructureWriteBit,
            sourceStageMask: PipelineStageAccelerationStructureBuildBit
        );
    }
    /// <inheritdoc/>
    public void WriteInstance(
        nint instanceBufferMappedPointer,
        int index,
        float scaleX,
        float scaleY,
        float scaleZ,
        float worldCenterX,
        float worldCenterY,
        float worldCenterZ,
        uint instanceCustomIndex,
        uint visibilityMask,
        ulong blasDeviceAddress
    ) {
        if (0 == instanceBufferMappedPointer) {
            return;
        }

        var instance = ((VkAccelerationStructureInstanceKhr*)instanceBufferMappedPointer + index);
        // Row-major 3x4: per-axis scale on the diagonal, translation in the last column.
        var transform = instance->Transform;

        transform[0] = scaleX;
        transform[1] = 0.0f;
        transform[2] = 0.0f;
        transform[3] = worldCenterX;
        transform[4] = 0.0f;
        transform[5] = scaleY;
        transform[6] = 0.0f;
        transform[7] = worldCenterY;
        transform[8] = 0.0f;
        transform[9] = 0.0f;
        transform[10] = scaleZ;
        transform[11] = worldCenterZ;
        // Bitfield pair: custom index in the low 24 bits, visibility mask in the top 8.
        instance->InstanceCustomIndexAndMask = (instanceCustomIndex & 0x00FFFFFF) | (visibilityMask << 24);
        instance->SbtRecordOffsetAndFlags = 0;
        instance->AccelerationStructureReference = blasDeviceAddress;
    }

    private (nint BufferHandle, nint MemoryHandle) CreateBuffer(VulkanWorldAccelerationCreateRequest request, ulong sizeBytes, uint usage, bool hostVisible) {
        return accelerationStructureApi.CreateBuffer(request: new VulkanAccelerationBufferCreateRequest(
            DeviceHandle: request.DeviceHandle,
            HostVisible: hostVisible,
            InstanceHandle: request.InstanceHandle,
            PhysicalDeviceHandle: request.PhysicalDeviceHandle,
            SizeBytes: sizeBytes,
            Usage: usage
        ));
    }
    private (nint BufferHandle, nint MemoryHandle, ulong AlignedDeviceAddress) CreateScratchBuffer(
        VulkanWorldAccelerationCreateRequest request,
        ulong scratchSize,
        uint scratchAlignment
    ) {
        // Over-allocate by the alignment and round the device address up: the spec aligns
        // the SCRATCH ADDRESS (minAccelerationStructureScratchOffsetAlignment), not the
        // buffer object.
        var alignment = Math.Max(
            val1: scratchAlignment,
            val2: 1u
        );

        var (bufferHandle, memoryHandle) = CreateBuffer(
            hostVisible: false,
            request: request,
            sizeBytes: (scratchSize + alignment),
            usage: BufferUsageStorageBufferBit | BufferUsageShaderDeviceAddressBit
        );
        var address = accelerationStructureApi.GetBufferDeviceAddress(
            bufferHandle: bufferHandle,
            deviceHandle: request.DeviceHandle
        );
        var alignedAddress = ((address + alignment) - 1) & ~((ulong)alignment - 1);

        return (bufferHandle, memoryHandle, alignedAddress);
    }
    private static VkAccelerationStructureGeometryAabbsKhr CreateAabbGeometry(ulong aabbDeviceAddress) {
        return new VkAccelerationStructureGeometryAabbsKhr {
            AabbsSType = StructureTypeAccelerationStructureGeometryAabbsDataKhr,
            DataDeviceAddress = aabbDeviceAddress,
            GeometryType = GeometryTypeAabbs,
            SType = StructureTypeAccelerationStructureGeometryKhr,
            Stride = AabbByteSize,
        };
    }
    private static VkAccelerationStructureGeometryInstancesKhr CreateInstancesGeometry(ulong instanceBufferDeviceAddress) {
        return new VkAccelerationStructureGeometryInstancesKhr {
            DataDeviceAddress = instanceBufferDeviceAddress,
            GeometryType = GeometryTypeInstances,
            InstancesSType = StructureTypeAccelerationStructureGeometryInstancesDataKhr,
            SType = StructureTypeAccelerationStructureGeometryKhr,
        };
    }
}
