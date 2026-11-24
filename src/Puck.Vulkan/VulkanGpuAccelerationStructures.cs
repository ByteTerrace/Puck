using Puck.Maths;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuAccelerationStructures"/> for Vulkan through <c>VK_KHR_acceleration_structure</c>, on a
/// device created with ray queries (<see cref="VulkanDeviceCommands.Rays"/>): a structure is a
/// <c>VkAccelerationStructureKHR</c> over a device-local buffer of its own, and every address a build reads comes from
/// <c>vkGetBufferDeviceAddress</c>. The build, compaction and copy commands <see cref="VulkanGpuRecorder"/> records
/// are this type's <see cref="RecordBuild"/>, <see cref="RecordCompactedSize"/> and <see cref="RecordCompact"/>.
/// </summary>
/// <param name="deviceContext">The device context every structure is created on.</param>
/// <param name="bufferApi">The native buffer API a structure's storage and a build's scratch are created through.</param>
/// <param name="naming">The naming every created buffer is handed to.</param>
public sealed unsafe class VulkanGpuAccelerationStructures(IVulkanDeviceContext deviceContext, IVulkanBufferApi bufferApi, GpuObjectNaming naming) : IGpuAccelerationStructures {
    // vulkan_core.h, SDK 1.4.363.
    private const uint StructureTypeCreateInfo = 1000150017;
    private const uint StructureTypeBuildGeometryInfo = 1000150000;
    private const uint StructureTypeGeometry = 1000150006;
    private const uint StructureTypeTrianglesData = 1000150005;
    private const uint StructureTypeInstancesData = 1000150004;
    private const uint StructureTypeBuildSizesInfo = 1000150020;
    private const uint StructureTypeCopyInfo = 1000150010;
    private const uint StructureTypeStructureAddressInfo = 1000150002;
    private const uint StructureTypeBufferAddressInfo = 1000244001;
    private const uint StructureTypeWriteDescriptorSet = 35;
    private const uint StructureTypeWriteDescriptorSetAccelerationStructure = 1000150007;
    private const uint StructureTypeQueryPoolCreateInfo = 11;
    private const uint TypeTopLevel = 0;
    private const uint TypeBottomLevel = 1;
    private const uint GeometryTypeTriangles = 0;
    private const uint GeometryTypeInstances = 2;
    private const uint GeometryOpaque = 0x1;
    private const uint BuildAllowCompaction = 0x2;
    private const uint BuildPreferFastTrace = 0x4;
    private const uint BuildTypeDevice = 1;
    private const uint FormatR32G32B32Float = 106;
    private const uint IndexTypeUint32 = 1;
    private const uint CopyModeCompact = 1;
    private const uint QueryTypeCompactedSize = 1000150000;
    private const uint QueryResult64BitWait = (0x1 | 0x2);
    // The largest minAccelerationStructureScratchOffsetAlignment any driver reports is well under this; the scratch size
    // Size reports carries this much slack, and RecordBuild rounds the scratch's address up to it.
    private const ulong ScratchAlignment = 256UL;

    private VulkanDeviceCommands Device => deviceContext.LogicalDevice.Commands;
    private VulkanRayCommands Rays => (Device.Rays ?? throw new NotSupportedException(message: "This Vulkan device was created without ray queries (GpuOptionalCapabilities.RayQuery)."));


    /// <inheritdoc/>
    public GpuAccelerationStructureSizes Size(in GpuAccelerationStructureInputs inputs) {
        var rays = Rays;
        var geometry = Geometry(inputs: in inputs, addresses: false);
        var info = BuildInfo(inputs: in inputs, geometry: &geometry, destination: 0, scratchAddress: 0UL);
        var sizes = new VkAccelerationStructureBuildSizesInfoKhr { SType = StructureTypeBuildSizesInfo };
        var count = inputs.Count;

        rays.GetBuildSizes(Device.Handle, BuildTypeDevice, in info, in count, ref sizes);

        return new GpuAccelerationStructureSizes(ScratchBytes: (sizes.BuildScratchSize + ScratchAlignment), StructureBytes: sizes.AccelerationStructureSize);
    }
    /// <inheritdoc/>
    public IGpuAccelerationStructure Create(GpuAccelerationStructureLevel level, ulong sizeBytes, in GpuObjectName name) {
        ArgumentOutOfRangeException.ThrowIfZero(value: sizeBytes);
        var rays = Rays;
        var storage = VulkanBuffer.Create(bufferApi: bufferApi, device: deviceContext, family: name.MemoryFamily(), memory: VulkanBufferMemory.DeviceLocal,
            sizeBytes: sizeBytes, usage: (VulkanBufferUsageFlags.AccelerationStructureStorage | VulkanBufferUsageFlags.ShaderDeviceAddress));

        try {
            naming.Name(handle: storage.BufferHandle, kind: GpuObjectKind.Buffer, name: in name);
            var info = new VkAccelerationStructureCreateInfoKhr {
                Buffer = storage.BufferHandle,
                SType = StructureTypeCreateInfo,
                Size = sizeBytes,
                Type = ((level == GpuAccelerationStructureLevel.Top) ? TypeTopLevel : TypeBottomLevel),
            };

            rays.CreateAccelerationStructure(Device.Handle, in info, 0, out var handle).ThrowIfFailed(device: Device, operation: "vkCreateAccelerationStructureKHR");

            var addressInfo = new VkHandleAddressInfo { Handle = handle, SType = StructureTypeStructureAddressInfo };

            return new Structure(device: Device, handle: handle, level: level, storage: storage, address: rays.GetAccelerationStructureAddress(Device.Handle, in addressInfo));
        } catch {
            storage.Dispose();
            throw;
        }
    }


    /// <inheritdoc/>
    public void WriteDescriptor(nint descriptorSetHandle, uint binding, IGpuAccelerationStructure structure) {
        var top = Of(structure: structure);
        _ = Rays;
        if (top.Level != GpuAccelerationStructureLevel.Top) {
            throw new ArgumentException(message: "A kernel traces a top level.", paramName: nameof(structure));
        }

        var handle = top.Handle;
        var extension = new VkWriteDescriptorSetAccelerationStructureKhr {
            AccelerationStructureCount = 1,
            PAccelerationStructures = ((nint)(&handle)),
            SType = StructureTypeWriteDescriptorSetAccelerationStructure,
        };
        var write = new VkWriteDescriptorSet {
            DescriptorCount = 1,
            DescriptorType = VulkanDescriptorType.AccelerationStructure,
            DstBinding = binding,
            DstSet = descriptorSetHandle,
            PNext = ((nint)(&extension)),
            SType = StructureTypeWriteDescriptorSet,
        };

        Device.UpdateDescriptorSets(Device.Handle, 1, ((nint)(&write)), 0, 0);
    }

    /// <summary>Records a build of a structure over its inputs, without barriers.</summary>
    /// <param name="device">The device's command table.</param>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="inputs">What the build reads.</param>
    /// <param name="destination">The structure written.</param>
    /// <param name="scratch">The scratch the build writes.</param>
    /// <exception cref="NotSupportedException">The device was created without ray queries.</exception>
    public static void RecordBuild(VulkanDeviceCommands device, nint commandBufferHandle, in GpuAccelerationStructureInputs inputs, IGpuAccelerationStructure destination, IGpuBuffer scratch) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: scratch);
        var rays = (device.Rays ?? throw new NotSupportedException(message: "This Vulkan device was created without ray queries."));
        var structure = Of(structure: destination);
        var geometry = Geometry(device: device, inputs: in inputs);
        var scratchAddress = BufferAddress(device: device, buffer: scratch.BufferHandle).AlignUp(alignment: ScratchAlignment);
        var info = BuildInfo(inputs: in inputs, geometry: &geometry, destination: structure.Handle, scratchAddress: scratchAddress);
        var range = new VkAccelerationStructureBuildRangeInfoKhr { PrimitiveCount = inputs.Count };
        var ranges = ((nint)(&range));

        rays.CmdBuild(commandBufferHandle, 1, in info, in ranges);
    }
    /// <summary>Records the write of a built structure's compacted size into a buffer through the structure's own query,
    /// without barriers.</summary>
    /// <param name="device">The device's command table.</param>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="source">The built structure.</param>
    /// <param name="destination">The buffer written, a transfer destination.</param>
    /// <param name="destinationOffsetBytes">The byte offset written.</param>
    /// <exception cref="NotSupportedException">The device was created without ray queries.</exception>
    public static void RecordCompactedSize(VulkanDeviceCommands device, nint commandBufferHandle, IGpuAccelerationStructure source, IGpuBuffer destination, ulong destinationOffsetBytes) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: destination);
        var rays = (device.Rays ?? throw new NotSupportedException(message: "This Vulkan device was created without ray queries."));
        var structure = Of(structure: source);
        var pool = structure.QueryPool();
        var handle = structure.Handle;

        device.Timestamps.CmdResetQueryPool(commandBufferHandle, pool, 0, 1);
        rays.CmdWriteProperties(commandBufferHandle, 1, in handle, QueryTypeCompactedSize, pool, 0);
        device.Timestamps.CmdCopyQueryPoolResults(commandBufferHandle, pool, 0, 1, destination.BufferHandle, destinationOffsetBytes, sizeof(ulong), QueryResult64BitWait);
    }
    /// <summary>Records a compacting copy of one structure into another, without barriers.</summary>
    /// <param name="device">The device's command table.</param>
    /// <param name="commandBufferHandle">The command buffer being recorded.</param>
    /// <param name="source">The built structure.</param>
    /// <param name="destination">The structure written.</param>
    /// <exception cref="NotSupportedException">The device was created without ray queries.</exception>
    public static void RecordCompact(VulkanDeviceCommands device, nint commandBufferHandle, IGpuAccelerationStructure source, IGpuAccelerationStructure destination) {
        ArgumentNullException.ThrowIfNull(argument: device);
        var rays = (device.Rays ?? throw new NotSupportedException(message: "This Vulkan device was created without ray queries."));
        var info = new VkCopyAccelerationStructureInfoKhr {
            Dst = Of(structure: destination).Handle,
            Mode = CopyModeCompact,
            Src = Of(structure: source).Handle,
            SType = StructureTypeCopyInfo,
        };

        rays.CmdCopy(commandBufferHandle, in info);
    }

    private static Structure Of(IGpuAccelerationStructure structure) {
        ArgumentNullException.ThrowIfNull(argument: structure);

        return ((structure as Structure) ?? throw new ArgumentException(message: "The structure was not created by this Vulkan device.", paramName: nameof(structure)));
    }
    private static ulong BufferAddress(VulkanDeviceCommands device, nint buffer) {
        var info = new VkHandleAddressInfo { Handle = buffer, SType = StructureTypeBufferAddressInfo };

        return device.Rays!.GetBufferAddress(device.Handle, in info);
    }
    private VkAccelerationStructureGeometryKhr Geometry(in GpuAccelerationStructureInputs inputs, bool addresses) =>
        addresses ? Geometry(device: Device, inputs: in inputs) : Describe(inputs: in inputs, sourceAddress: 0UL);
    private static VkAccelerationStructureGeometryKhr Geometry(VulkanDeviceCommands device, in GpuAccelerationStructureInputs inputs) {
        ArgumentNullException.ThrowIfNull(argument: inputs.Source);

        return Describe(inputs: in inputs, sourceAddress: BufferAddress(device: device, buffer: inputs.Source.BufferHandle));
    }
    private static VkAccelerationStructureGeometryKhr Describe(in GpuAccelerationStructureInputs inputs, ulong sourceAddress) {
        if (inputs.Level == GpuAccelerationStructureLevel.Top) {
            return new VkAccelerationStructureGeometryKhr {
                DataSType = StructureTypeInstancesData,
                Flags = GeometryOpaque,
                GeometryType = GeometryTypeInstances,
                SType = StructureTypeGeometry,
                VertexDataOrInstanceData = ((sourceAddress == 0UL) ? 0UL : (sourceAddress + inputs.InstanceOffsetBytes)),
            };
        }

        return new VkAccelerationStructureGeometryKhr {
            DataSType = StructureTypeTrianglesData,
            Flags = GeometryOpaque,
            GeometryType = GeometryTypeTriangles,
            IndexData = ((sourceAddress == 0UL) ? 0UL : (sourceAddress + inputs.IndexOffsetBytes)),
            IndexType = IndexTypeUint32,
            MaxVertex = ((inputs.VertexCount == 0U) ? 0U : (inputs.VertexCount - 1U)),
            SType = StructureTypeGeometry,
            VertexDataOrInstanceData = ((sourceAddress == 0UL) ? 0UL : (sourceAddress + inputs.VertexOffsetBytes)),
            VertexFormatOrArrayOfPointers = FormatR32G32B32Float,
            VertexStride = inputs.VertexStrideBytes,
        };
    }
    private static VkAccelerationStructureBuildGeometryInfoKhr BuildInfo(in GpuAccelerationStructureInputs inputs, VkAccelerationStructureGeometryKhr* geometry, nint destination, ulong scratchAddress) {
        var top = (inputs.Level == GpuAccelerationStructureLevel.Top);

        return new VkAccelerationStructureBuildGeometryInfoKhr {
            DstAccelerationStructure = destination,
            Flags = (top ? BuildPreferFastTrace : (BuildPreferFastTrace | BuildAllowCompaction)),
            GeometryCount = 1,
            PGeometries = ((nint)geometry),
            ScratchData = scratchAddress,
            SType = StructureTypeBuildGeometryInfo,
            Type = (top ? TypeTopLevel : TypeBottomLevel),
        };
    }

    // One structure: its handle, its storage buffer, its address and, once its compacted size is asked for, the query
    // pool of one query that answers it. Disposal destroys all three.
    private sealed class Structure(VulkanDeviceCommands device, nint handle, GpuAccelerationStructureLevel level, VulkanBuffer storage, ulong address) : IGpuAccelerationStructure {
        private nint m_handle = handle;
        private nint m_queryPool;

        public GpuAccelerationStructureLevel Level { get; } = level;
        public ulong SizeBytes => storage.SizeBytes;
        public ulong Address { get; } = address;
        public nint Handle {
            get {
                ObjectDisposedException.ThrowIf(condition: (m_handle == 0), instance: this);
                return m_handle;
            }
        }

        public nint QueryPool() {
            if (m_queryPool == 0) {
                var info = new VkQueryPoolCreateInfo { QueryCount = 1, QueryType = QueryTypeCompactedSize, SType = StructureTypeQueryPoolCreateInfo };

                device.Timestamps.CreateQueryPool(device.Handle, in info, 0, out m_queryPool).ThrowIfFailed(device: device, operation: "vkCreateQueryPool");
            }
            return m_queryPool;
        }
        public void Dispose() {
            if (m_handle == 0) { return; }
            if (m_queryPool != 0) { device.Timestamps.DestroyQueryPool(device.Handle, m_queryPool, 0); m_queryPool = 0; }
            device.Rays!.DestroyAccelerationStructure(device.Handle, m_handle, 0);
            m_handle = 0;
            storage.Dispose();
        }
    }
}
