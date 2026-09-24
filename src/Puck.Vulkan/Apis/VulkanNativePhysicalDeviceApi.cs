using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanPhysicalDeviceApi"/>, marshaling to the physical-device
/// enumeration and query entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativePhysicalDeviceApi : IVulkanPhysicalDeviceApi {
    // VkPhysicalDeviceProperties field offsets: apiVersion(0), driverVersion(4), vendorID(8), deviceID(12),
    // deviceType(16), deviceName[VK_MAX_PHYSICAL_DEVICE_NAME_SIZE=256](20), pipelineCacheUUID[VK_UUID_SIZE=16](276).
    private const int PhysicalDeviceApiVersionOffset = 0;
    private const int PhysicalDeviceDriverVersionOffset = sizeof(uint);
    private const int PhysicalDeviceIdOffset = (sizeof(uint) * 3);
    private const int PhysicalDeviceVendorIdOffset = (sizeof(uint) * 2);
    // Vulkan 1.2, from which VkPhysicalDeviceDriverProperties is core.
    private const uint VulkanVersion12 = (1U << 22) | (2U << 12);
    private const int PhysicalDevicePipelineCacheUuidLength = 16;
    private const int PhysicalDevicePipelineCacheUuidOffset = (PhysicalDeviceNameOffset + 256);
    // VkPhysicalDeviceFeatures is 55 consecutive VkBool32 fields.
    private const int PhysicalDeviceFeatureCount = 55;
    private const int PhysicalDeviceNameOffset = (sizeof(uint) * 5);
    private const int PhysicalDevicePropertiesBufferSize = 2048;
    private const int PhysicalDeviceTypeOffset = (sizeof(uint) * 4);
    private const uint StructureTypePhysicalDeviceDriverProperties = 1000196000;
    private const uint StructureTypePhysicalDeviceFeatures2 = 1000059000;
    private const uint StructureTypePhysicalDeviceIdProperties = 1000071004;
    private const uint StructureTypePhysicalDeviceProperties2 = 1000059001;

    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativePhysicalDeviceApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativePhysicalDeviceApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private static unsafe void ValidatePhysicalDeviceInputs(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        VulkanArgument.RequireHandle(
            handle: physicalDeviceHandle,
            handleDescription: "physical-device",
            paramName: nameof(physicalDeviceHandle)
        );
    }
    private static unsafe void ValidatePhysicalDeviceSurfaceInputs(
        VulkanInstanceCommands instance,
        nint physicalDeviceHandle,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        VulkanArgument.RequireHandle(
            handle: surfaceHandle,
            handleDescription: "surface",
            paramName: nameof(surfaceHandle)
        );
    }

    /// <inheritdoc/>
    public IReadOnlyList<nint> EnumeratePhysicalDevices(VulkanInstanceCommands instance) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        var enumeratePhysicalDevices = instance.EnumeratePhysicalDevices;

        var physicalDeviceCount = 0U;
        var result = enumeratePhysicalDevices(
            instance.Handle,
            ref physicalDeviceCount,
            0
        );

        result.ThrowIfFailed(operation: "vkEnumeratePhysicalDevices");

        if (0 == physicalDeviceCount) {
            return [];
        }

        var deviceBuffer = m_allocator.Alloc(size: (IntPtr.Size * checked((int)physicalDeviceCount)));

        try {
            result = enumeratePhysicalDevices(
                instance.Handle,
                ref physicalDeviceCount,
                deviceBuffer
            );
            result.ThrowIfFailed(operation: "vkEnumeratePhysicalDevices");

            var physicalDevices = new nint[physicalDeviceCount];

            for (var index = 0; (index < physicalDevices.Length); index++) {
                physicalDevices[index] = Marshal.ReadIntPtr(
                    ofs: (index * IntPtr.Size),
                    ptr: deviceBuffer
                );
            }

            return physicalDevices;
        } finally {
            m_allocator.Free(ptr: deviceBuffer);
        }
    }
    /// <inheritdoc/>
    public uint GetDeviceApiVersion(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceProperties = instance.GetPhysicalDeviceProperties;

        var propertiesBuffer = m_allocator.Alloc(size: PhysicalDevicePropertiesBufferSize);

        try {
            getPhysicalDeviceProperties(
                physicalDeviceHandle,
                propertiesBuffer
            );

            return unchecked((uint)Marshal.ReadInt32(
                ofs: PhysicalDeviceApiVersionOffset,
                ptr: propertiesBuffer
            ));
        } finally {
            m_allocator.Free(ptr: propertiesBuffer);
        }
    }
    /// <inheritdoc/>
    public GpuDeviceIdentity GetDeviceIdentity(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var driverProperties = new VkPhysicalDeviceDriverProperties {
            SType = StructureTypePhysicalDeviceDriverProperties,
        };
        var properties2 = new VkPhysicalDeviceProperties2 {
            SType = StructureTypePhysicalDeviceProperties2,
        };
        var apiVersion = GetDeviceApiVersion(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );
        var hasDriverProperties = (
            (instance.GetPhysicalDeviceProperties2 is not null) && (
                (apiVersion >= VulkanVersion12) ||
                HasDeviceExtension(
                    extensionName: "VK_KHR_driver_properties",
                    instance: instance,
                    physicalDeviceHandle: physicalDeviceHandle
                )
            )
        );

        if (hasDriverProperties) {
            properties2.PNext = ((nint)(&driverProperties));
            instance.GetPhysicalDeviceProperties2(
                physicalDeviceHandle,
                ((nint)(&properties2))
            );
        } else {
            instance.GetPhysicalDeviceProperties(
                physicalDeviceHandle,
                ((nint)properties2.Properties)
            );
        }

        var properties = properties2.Properties;
        var driverVersion = *((uint*)(properties + PhysicalDeviceDriverVersionOffset));

        return new GpuDeviceIdentity(
            AdapterName: (Marshal.PtrToStringUTF8(ptr: ((nint)(properties + PhysicalDeviceNameOffset))) ?? string.Empty),
            ApiVersion: GpuDeviceIdentity.FormatVulkanVersion(packed: *((uint*)(properties + PhysicalDeviceApiVersionOffset))),
            Backend: "vulkan",
            ConformanceVersion: (hasDriverProperties
                ? $"{driverProperties.ConformanceMajor}.{driverProperties.ConformanceMinor}.{driverProperties.ConformanceSubminor}.{driverProperties.ConformancePatch}"
                : string.Empty
            ),
            DeviceId: *((uint*)(properties + PhysicalDeviceIdOffset)),
            DriverId: (hasDriverProperties
                ? driverProperties.DriverId
                : 0U
            ),
            DriverName: (hasDriverProperties
                ? (Marshal.PtrToStringUTF8(ptr: ((nint)driverProperties.DriverName)) ?? string.Empty)
                : string.Empty
            ),
            DriverVersion: (hasDriverProperties
                ? (Marshal.PtrToStringUTF8(ptr: ((nint)driverProperties.DriverInfo)) ?? string.Empty)
                : string.Empty
            ),
            DriverVersionRaw: driverVersion,
            PipelineCacheUuid: Convert.ToHexStringLower(bytes: new ReadOnlySpan<byte>(
                length: PhysicalDevicePipelineCacheUuidLength,
                pointer: (properties + PhysicalDevicePipelineCacheUuidOffset)
            )),
            VendorId: *((uint*)(properties + PhysicalDeviceVendorIdOffset))
        );
    }
    /// <inheritdoc/>
    public long GetDeviceLuid(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceProperties2 = instance.GetPhysicalDeviceProperties2;

        if (getPhysicalDeviceProperties2 is null) {
            return 0;
        }

        var idProperties = new VkPhysicalDeviceIDProperties {
            SType = StructureTypePhysicalDeviceIdProperties,
        };
        var properties2 = new VkPhysicalDeviceProperties2 {
            PNext = ((nint)(&idProperties)),
            SType = StructureTypePhysicalDeviceProperties2,
        };

        getPhysicalDeviceProperties2(
            physicalDeviceHandle,
            ((nint)(&properties2))
        );

        if (0 == idProperties.DeviceLuidValid) {
            return 0;
        }

        // The 8-byte LUID is laid out exactly as a Win32 LUID (LowPart then HighPart), matching DxgiInterop's
        // packing, so reading it as a little-endian long yields a value directly comparable to a DXGI adapter LUID.
        return *((long*)idProperties.DeviceLuid);
    }
    /// <inheritdoc/>
    public GpuMemoryProfile GetMemoryProfile(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );
        instance.GetPhysicalDeviceMemoryProperties(
            physicalDeviceHandle,
            out var memory
        );

        return GpuMemoryProfile.FromVulkan(
            deviceType: ((uint)GetPhysicalDeviceType(
                instance: instance,
                physicalDeviceHandle: physicalDeviceHandle
            )),
            memoryHeaps: new ReadOnlySpan<ulong>(
                length: (((int)memory.MemoryHeapCount) * 2),
                pointer: memory.MemoryHeapPairs
            ),
            memoryTypes: new ReadOnlySpan<uint>(
                length: (((int)memory.MemoryTypeCount) * 2),
                pointer: memory.MemoryTypePairs
            )
        );
    }
    /// <inheritdoc/>
    public string GetDeviceName(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceProperties = instance.GetPhysicalDeviceProperties;

        var propertiesBuffer = m_allocator.Alloc(size: PhysicalDevicePropertiesBufferSize);

        try {
            getPhysicalDeviceProperties(
                physicalDeviceHandle,
                propertiesBuffer
            );

            // deviceName is a NUL-terminated UTF-8 char[256]; PtrToStringUTF8 stops at the terminator.
            return (Marshal.PtrToStringUTF8(ptr: IntPtr.Add(
                offset: PhysicalDeviceNameOffset,
                pointer: propertiesBuffer
            )) ?? "(unknown device)");
        } finally {
            m_allocator.Free(ptr: propertiesBuffer);
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<bool> GetFeatureSupport(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceFeatures = instance.GetPhysicalDeviceFeatures;
        var featureBytes = (PhysicalDeviceFeatureCount * sizeof(uint));
        var buffer = m_allocator.Alloc(size: featureBytes);

        try {
            getPhysicalDeviceFeatures(
                physicalDeviceHandle,
                buffer
            );

            var support = new bool[PhysicalDeviceFeatureCount];

            for (var index = 0; (index < PhysicalDeviceFeatureCount); index++) {
                support[index] = (0 != Marshal.ReadInt32(
                    ofs: (index * sizeof(uint)),
                    ptr: buffer
                ));
            }

            return support;
        } finally {
            m_allocator.Free(ptr: buffer);
        }
    }
    /// <inheritdoc/>
    public VkPhysicalDeviceType GetPhysicalDeviceType(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceProperties = instance.GetPhysicalDeviceProperties;

        var propertiesBuffer = m_allocator.Alloc(size: PhysicalDevicePropertiesBufferSize);

        try {
            getPhysicalDeviceProperties(
                physicalDeviceHandle,
                propertiesBuffer
            );
            var deviceType = Marshal.ReadInt32(
                ofs: PhysicalDeviceTypeOffset,
                ptr: propertiesBuffer
            );

            return (Enum.IsDefined(
                enumType: typeof(VkPhysicalDeviceType),
                value: deviceType
            )
                ? (VkPhysicalDeviceType)deviceType
                : VkPhysicalDeviceType.Other
            );
        } finally {
            m_allocator.Free(ptr: propertiesBuffer);
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<uint> GetPresentModes(VulkanInstanceCommands instance, nint physicalDeviceHandle, nint surfaceHandle) {
        ValidatePhysicalDeviceSurfaceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getPresentModes = instance.GetPhysicalDeviceSurfacePresentModesKhr;

        if (getPresentModes is null) {
            throw VulkanProcResolver.MissingInstanceProc(functionName: "vkGetPhysicalDeviceSurfacePresentModesKHR"u8);
        }

        var modeCount = 0U;
        var result = getPresentModes(
            physicalDeviceHandle,
            surfaceHandle,
            ref modeCount,
            0
        );

        result.ThrowIfFailed(operation: "vkGetPhysicalDeviceSurfacePresentModesKHR");

        if (0 == modeCount) {
            return [];
        }

        var modeBuffer = m_allocator.Alloc(size: (sizeof(uint) * checked((int)modeCount)));

        try {
            result = getPresentModes(
                physicalDeviceHandle,
                surfaceHandle,
                ref modeCount,
                modeBuffer
            );
            result.ThrowIfFailed(operation: "vkGetPhysicalDeviceSurfacePresentModesKHR");

            var presentModes = new int[modeCount];

            Marshal.Copy(
                destination: presentModes,
                length: ((int)modeCount),
                source: modeBuffer,
                startIndex: 0
            );
            return Array.ConvertAll(
                array: presentModes,
                converter: static mode => unchecked((uint)mode)
            );
        } finally {
            m_allocator.Free(ptr: modeBuffer);
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<VkQueueFamilyInfo> GetQueueFamilies(VulkanInstanceCommands instance, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getQueueFamilyProperties = instance.GetPhysicalDeviceQueueFamilyProperties;

        var queueFamilyCount = 0U;

        getQueueFamilyProperties(
            physicalDeviceHandle,
            ref queueFamilyCount,
            0
        );

        if (0 == queueFamilyCount) {
            return [];
        }

        var structureSize = Marshal.SizeOf<VkQueueFamilyProperties>();
        var queueFamilyBuffer = m_allocator.Alloc(size: (structureSize * checked((int)queueFamilyCount)));

        try {
            getQueueFamilyProperties(
                physicalDeviceHandle,
                ref queueFamilyCount,
                queueFamilyBuffer
            );

            var queueFamilies = new VkQueueFamilyInfo[queueFamilyCount];

            for (var index = 0; (index < queueFamilies.Length); index++) {
                var properties = Marshal.PtrToStructure<VkQueueFamilyProperties>(ptr: IntPtr.Add(
                    offset: (index * structureSize),
                    pointer: queueFamilyBuffer
                ));

                queueFamilies[index] = new VkQueueFamilyInfo(
                    Flags: ((VkQueueFlags)properties.QueueFlags),
                    Index: ((uint)index),
                    QueueCount: properties.QueueCount
                );
            }

            return queueFamilies;
        } finally {
            m_allocator.Free(ptr: queueFamilyBuffer);
        }
    }
    /// <inheritdoc/>
    public VulkanSurfaceCapabilities GetSurfaceCapabilities(
        VulkanInstanceCommands instance,
        nint physicalDeviceHandle,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceSurfaceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getSurfaceCapabilities = instance.GetPhysicalDeviceSurfaceCapabilitiesKhr;

        if (getSurfaceCapabilities is null) {
            throw VulkanProcResolver.MissingInstanceProc(functionName: "vkGetPhysicalDeviceSurfaceCapabilitiesKHR"u8);
        }

        var result = getSurfaceCapabilities(
            physicalDeviceHandle,
            surfaceHandle,
            out var capabilities
        );

        result.ThrowIfFailed(operation: "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        return new VulkanSurfaceCapabilities(
            CurrentExtentHeight: capabilities.CurrentExtent.Height,
            CurrentExtentWidth: capabilities.CurrentExtent.Width,
            CurrentTransform: capabilities.CurrentTransform,
            MaxImageCount: capabilities.MaxImageCount,
            MaxImageExtentHeight: capabilities.MaxImageExtent.Height,
            MaxImageExtentWidth: capabilities.MaxImageExtent.Width,
            MinImageCount: capabilities.MinImageCount,
            MinImageExtentHeight: capabilities.MinImageExtent.Height,
            MinImageExtentWidth: capabilities.MinImageExtent.Width,
            SupportedCompositeAlpha: capabilities.SupportedCompositeAlpha
        );
    }
    /// <inheritdoc/>
    public IReadOnlyList<VulkanSurfaceFormat> GetSurfaceFormats(
        VulkanInstanceCommands instance,
        nint physicalDeviceHandle,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceSurfaceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getSurfaceFormats = instance.GetPhysicalDeviceSurfaceFormatsKhr;

        if (getSurfaceFormats is null) {
            throw VulkanProcResolver.MissingInstanceProc(functionName: "vkGetPhysicalDeviceSurfaceFormatsKHR"u8);
        }

        var formatCount = 0U;
        var result = getSurfaceFormats(
            physicalDeviceHandle,
            surfaceHandle,
            ref formatCount,
            0
        );

        result.ThrowIfFailed(operation: "vkGetPhysicalDeviceSurfaceFormatsKHR");

        if (0 == formatCount) {
            return [];
        }

        var structureSize = Marshal.SizeOf<VkSurfaceFormatKhr>();
        var formatBuffer = m_allocator.Alloc(size: (structureSize * checked((int)formatCount)));

        try {
            result = getSurfaceFormats(
                physicalDeviceHandle,
                surfaceHandle,
                ref formatCount,
                formatBuffer
            );
            result.ThrowIfFailed(operation: "vkGetPhysicalDeviceSurfaceFormatsKHR");

            var surfaceFormats = new VulkanSurfaceFormat[formatCount];

            for (var index = 0; (index < surfaceFormats.Length); index++) {
                var format = Marshal.PtrToStructure<VkSurfaceFormatKhr>(ptr: IntPtr.Add(
                    offset: (index * structureSize),
                    pointer: formatBuffer
                ));

                surfaceFormats[index] = new VulkanSurfaceFormat(
                    ColorSpace: format.ColorSpace,
                    Format: format.Format
                );
            }

            return surfaceFormats;
        } finally {
            m_allocator.Free(ptr: formatBuffer);
        }
    }
    /// <inheritdoc/>
    public bool GetSurfaceSupport(
        VulkanInstanceCommands instance,
        nint physicalDeviceHandle,
        uint queueFamilyIndex,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceSurfaceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getSurfaceSupport = instance.GetPhysicalDeviceSurfaceSupportKhr;

        if (getSurfaceSupport is null) {
            throw VulkanProcResolver.MissingInstanceProc(functionName: "vkGetPhysicalDeviceSurfaceSupportKHR"u8);
        }

        var result = getSurfaceSupport(
            physicalDeviceHandle,
            queueFamilyIndex,
            surfaceHandle,
            out var supported
        );

        result.ThrowIfFailed(operation: "vkGetPhysicalDeviceSurfaceSupportKHR");
        return (0 != supported);
    }
    /// <inheritdoc/>
    public bool HasDeviceExtension(VulkanInstanceCommands instance, nint physicalDeviceHandle, string extensionName) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );
        ArgumentException.ThrowIfNullOrEmpty(argument: extensionName);

        var enumerateDeviceExtensionProperties = instance.EnumerateDeviceExtensionProperties;

        var count = 0U;

        if (
            (VkResult.Success != enumerateDeviceExtensionProperties(
            physicalDeviceHandle,
            0,
            ((nint)(&count)),
            0
        )) ||
            (0 == count)
        ) {
            return false;
        }

        var properties = new VkExtensionProperties[count];

        fixed (VkExtensionProperties* propertiesPointer = properties) {
            // A second enumeration can legitimately return Incomplete if the extension
            // list grew between calls; the entries that were written are still valid.
            var result = enumerateDeviceExtensionProperties(
                physicalDeviceHandle,
                0,
                ((nint)(&count)),
                ((nint)propertiesPointer)
            );

            if (
                (VkResult.Success != result) &&
                (VkResult.Incomplete != result)
            ) {
                return false;
            }

            for (var index = 0; (index < count); index++) {
                var name = Marshal.PtrToStringUTF8(ptr: ((nint)propertiesPointer[index].ExtensionName));

                if (string.Equals(
                    a: name,
                    b: extensionName,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return true;
                }
            }
        }

        return false;
    }
    /// <inheritdoc/>
    public bool IsExtensionFeatureSupported(VulkanInstanceCommands instance, nint physicalDeviceHandle, uint structureType) {
        ValidatePhysicalDeviceInputs(
            instance: instance,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceFeatures2 = instance.GetPhysicalDeviceFeatures2;

        if (getPhysicalDeviceFeatures2 is null) {
            return false;
        }

        // Every VkPhysicalDevice*FeaturesKHR struct shares the layout
        // { uint sType; nint pNext; VkBool32 <feature>; }, so the first feature flag sits
        // at offset 16 on the 64-bit ABI — probe it generically without naming the struct.
        const int FeatureFlagOffset = 16;
        // vkGetPhysicalDeviceFeatures2 writes the WHOLE *FeaturesKHR struct for this structureType, not just the first
        // flag — and some run well past 24 bytes. A
        // 24-byte buffer let the driver write past the stack allocation; 256 matches the size already proven defensive
        // in VulkanNativeLogicalDeviceApi (which documents the same prior bug).
        const int FeatureBlockByteSize = 256;

        var featureBlock = stackalloc byte[FeatureBlockByteSize];

        new Span<byte>(
            length: FeatureBlockByteSize,
            pointer: featureBlock
        ).Clear();
        *((uint*)featureBlock) = structureType;
        var features2 = new VkPhysicalDeviceFeatures2 {
            PNext = ((nint)featureBlock),
            SType = StructureTypePhysicalDeviceFeatures2,
        };

        getPhysicalDeviceFeatures2(
            physicalDeviceHandle,
            ((nint)(&features2))
        );
        return (0 != *((uint*)(featureBlock + FeatureFlagOffset)));
    }
}
