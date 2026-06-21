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
    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativePhysicalDeviceApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativePhysicalDeviceApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    private const int PhysicalDevicePropertiesBufferSize = 2048;
    private const int PhysicalDeviceTypeOffset = (sizeof(uint) * 4);
    // VkPhysicalDeviceFeatures is 55 consecutive VkBool32 fields.
    private const int PhysicalDeviceFeatureCount = 55;
    private const uint StructureTypePhysicalDeviceFeatures2 = 1000059000;
    private const uint StructureTypePhysicalDeviceIdProperties = 1000071004;
    private const uint StructureTypePhysicalDeviceProperties2 = 1000059001;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

    /// <inheritdoc/>
    public IReadOnlyList<nint> EnumeratePhysicalDevices(nint instanceHandle) {
        if (0 == instanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(instanceHandle)
            );
        }

        var enumeratePhysicalDevices = GetPointers(instanceHandle: instanceHandle).EnumeratePhysicalDevices;

        var physicalDeviceCount = 0U;
        var result = enumeratePhysicalDevices(
            instanceHandle,
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
                instanceHandle,
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
    public VkPhysicalDeviceType GetPhysicalDeviceType(nint instanceHandle, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceProperties = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceProperties;

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
                : VkPhysicalDeviceType.Other);
        } finally {
            m_allocator.Free(ptr: propertiesBuffer);
        }
    }
    /// <inheritdoc/>
    public long GetDeviceLuid(nint instanceHandle, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceProperties2 = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceProperties2;

        if (getPhysicalDeviceProperties2 is null) {
            return 0;
        }

        var idProperties = new VkPhysicalDeviceIDProperties {
            SType = StructureTypePhysicalDeviceIdProperties,
        };
        var properties2 = new VkPhysicalDeviceProperties2 {
            PNext = (nint)(&idProperties),
            SType = StructureTypePhysicalDeviceProperties2,
        };

        getPhysicalDeviceProperties2(
            physicalDeviceHandle,
            (nint)(&properties2)
        );

        if (0 == idProperties.DeviceLuidValid) {
            return 0;
        }

        // The 8-byte LUID is laid out exactly as a Win32 LUID (LowPart then HighPart), matching DxgiInterop's
        // packing, so reading it as a little-endian long yields a value directly comparable to a DXGI adapter LUID.
        return *(long*)idProperties.DeviceLuid;
    }
    /// <inheritdoc/>
    public IReadOnlyList<VkQueueFamilyInfo> GetQueueFamilies(nint instanceHandle, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getQueueFamilyProperties = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceQueueFamilyProperties;

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
                    Flags: (VkQueueFlags)properties.QueueFlags,
                    Index: (uint)index,
                    QueueCount: properties.QueueCount
                );
            }

            return queueFamilies;
        } finally {
            m_allocator.Free(ptr: queueFamilyBuffer);
        }
    }
    /// <inheritdoc/>
    public bool GetSurfaceSupport(
        nint instanceHandle,
        nint physicalDeviceHandle,
        uint queueFamilyIndex,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceSurfaceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getSurfaceSupport = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceSurfaceSupportKhr;

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
    public VulkanSurfaceCapabilities GetSurfaceCapabilities(
        nint instanceHandle,
        nint physicalDeviceHandle,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceSurfaceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getSurfaceCapabilities = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceSurfaceCapabilitiesKhr;

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
        nint instanceHandle,
        nint physicalDeviceHandle,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceSurfaceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getSurfaceFormats = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceSurfaceFormatsKhr;

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
    public IReadOnlyList<uint> GetPresentModes(nint instanceHandle, nint physicalDeviceHandle, nint surfaceHandle) {
        ValidatePhysicalDeviceSurfaceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle,
            surfaceHandle: surfaceHandle
        );

        var getPresentModes = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceSurfacePresentModesKhr;

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
                length: (int)modeCount,
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
    public VulkanTimestampCapabilities GetTimestampCapabilities(
        nint instanceHandle,
        nint physicalDeviceHandle,
        uint graphicsQueueFamilyIndex
    ) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var pointers = GetPointers(instanceHandle: instanceHandle);

        // timestampPeriod (nanoseconds per tick) lives deep in VkPhysicalDeviceLimits; read
        // it through a fully-typed prefix so the compiler computes the offset from the field
        // ABI (the hardcoded-offset style used for deviceType is fragile across that many
        // 8-byte-aligned members).
        var propertiesBuffer = m_allocator.Alloc(size: PhysicalDevicePropertiesBufferSize);
        float timestampPeriod;

        try {
            pointers.GetPhysicalDeviceProperties(
                physicalDeviceHandle,
                propertiesBuffer
            );
            timestampPeriod = ((VkPhysicalDeviceProperties*)propertiesBuffer)->Limits.TimestampPeriod;
        } finally {
            m_allocator.Free(ptr: propertiesBuffer);
        }

        // Guard a layout/offset mistake from poisoning every measurement: timestampPeriod is
        // a small positive float on real hardware (NVIDIA reports exactly 1.0). Anything
        // outside a sane band means a misread — fall back to a 1 ns tick.
        if (!((timestampPeriod > 0.0f) && (timestampPeriod < 100_000.0f))) {
            timestampPeriod = 1.0f;
        }

        // timestampValidBits is per queue family; re-read the family table and pick the
        // render (graphics) family's value (0 => the queue cannot timestamp).
        var validBits = 0U;
        var queueFamilyCount = 0U;

        pointers.GetPhysicalDeviceQueueFamilyProperties(
            physicalDeviceHandle,
            ref queueFamilyCount,
            0
        );
        if (queueFamilyCount > 0) {
            var structureSize = Marshal.SizeOf<VkQueueFamilyProperties>();
            var queueFamilyBuffer = m_allocator.Alloc(size: (structureSize * checked((int)queueFamilyCount)));

            try {
                pointers.GetPhysicalDeviceQueueFamilyProperties(
                    physicalDeviceHandle,
                    ref queueFamilyCount,
                    queueFamilyBuffer
                );
                if (graphicsQueueFamilyIndex < queueFamilyCount) {
                    var properties = Marshal.PtrToStructure<VkQueueFamilyProperties>(ptr: IntPtr.Add(
                        offset: checked(((int)graphicsQueueFamilyIndex * structureSize)),
                        pointer: queueFamilyBuffer
                    ));

                    validBits = properties.TimestampValidBits;
                }
            } finally {
                m_allocator.Free(ptr: queueFamilyBuffer);
            }
        }

        return new VulkanTimestampCapabilities(
            GraphicsQueueValidBits: validBits,
            PeriodNanoseconds: timestampPeriod
        );
    }
    /// <inheritdoc/>
    public bool HasDeviceExtension(nint instanceHandle, nint physicalDeviceHandle, string extensionName) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );
        ArgumentException.ThrowIfNullOrEmpty(argument: extensionName);

        var enumerateDeviceExtensionProperties = GetPointers(instanceHandle: instanceHandle).EnumerateDeviceExtensionProperties;

        if (enumerateDeviceExtensionProperties is null) {
            return false;
        }

        var count = 0U;

        if (
            (VkResult.Success != enumerateDeviceExtensionProperties(
                physicalDeviceHandle,
                0,
                (nint)(&count),
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
                (nint)(&count),
                (nint)propertiesPointer
            );

            if (
                (VkResult.Success != result) &&
                (VkResult.Incomplete != result)
            ) {
                return false;
            }

            for (var index = 0; (index < count); index++) {
                var name = Marshal.PtrToStringUTF8(ptr: (nint)propertiesPointer[index].ExtensionName);

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
    public IReadOnlyList<bool> GetFeatureSupport(nint instanceHandle, nint physicalDeviceHandle) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceFeatures = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceFeatures;
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
    public bool IsExtensionFeatureSupported(nint instanceHandle, nint physicalDeviceHandle, uint structureType) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        var getPhysicalDeviceFeatures2 = GetPointers(instanceHandle: instanceHandle).GetPhysicalDeviceFeatures2;

        if (getPhysicalDeviceFeatures2 is null) {
            return false;
        }

        // Every VkPhysicalDevice*FeaturesKHR struct shares the layout
        // { uint sType; nint pNext; VkBool32 <feature>; }, so the first feature flag sits
        // at offset 16 on the 64-bit ABI — probe it generically without naming the struct.
        const int FeatureFlagOffset = 16;
        // vkGetPhysicalDeviceFeatures2 writes the WHOLE *FeaturesKHR struct for this structureType, not just the first
        // flag — and some run well past 24 bytes (e.g. VkPhysicalDeviceAccelerationStructureFeaturesKHR is 40+). A
        // 24-byte buffer let the driver write past the stack allocation; 256 matches the size already proven defensive
        // in VulkanNativeLogicalDeviceApi (which documents the same prior bug).
        const int FeatureBlockByteSize = 256;

        var featureBlock = stackalloc byte[FeatureBlockByteSize];

        new Span<byte>(
            length: FeatureBlockByteSize,
            pointer: featureBlock
        ).Clear();
        *(uint*)featureBlock = structureType;
        var features2 = new VkPhysicalDeviceFeatures2 {
            PNext = (nint)featureBlock,
            SType = StructureTypePhysicalDeviceFeatures2,
        };

        getPhysicalDeviceFeatures2(
            physicalDeviceHandle,
            (nint)(&features2)
        );
        return (0 != *(uint*)(featureBlock + FeatureFlagOffset));
    }

    private static unsafe void ValidatePhysicalDeviceInputs(nint instanceHandle, nint physicalDeviceHandle) {
        if (0 == instanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(instanceHandle)
            );
        }

        if (0 == physicalDeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan physical-device handle must be non-zero.",
                paramName: nameof(physicalDeviceHandle)
            );
        }
    }
    private static unsafe void ValidatePhysicalDeviceSurfaceInputs(
        nint instanceHandle,
        nint physicalDeviceHandle,
        nint surfaceHandle
    ) {
        ValidatePhysicalDeviceInputs(
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        if (0 == surfaceHandle) {
            throw new ArgumentException(
                message: "Vulkan surface handle must be non-zero.",
                paramName: nameof(surfaceHandle)
            );
        }
    }

    private unsafe struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, ref uint, nint, VkResult> EnumeratePhysicalDevices;
        public delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceProperties;
        public delegate* unmanaged[Cdecl]<nint, ref uint, nint, void> GetPhysicalDeviceQueueFamilyProperties;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, out uint, VkResult> GetPhysicalDeviceSurfaceSupportKhr;
        public delegate* unmanaged[Cdecl]<nint, nint, out VkSurfaceCapabilitiesKhr, VkResult> GetPhysicalDeviceSurfaceCapabilitiesKhr;
        public delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult> GetPhysicalDeviceSurfaceFormatsKhr;
        public delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult> GetPhysicalDeviceSurfacePresentModesKhr;
        public delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceFeatures;
        public delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceFeatures2;
        public delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceProperties2;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, nint, VkResult> EnumerateDeviceExtensionProperties;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, InstancePointers> m_pointers = new();

    private unsafe InstancePointers GetPointers(nint instanceHandle) {
        if (m_pointers.TryGetValue(
            key: instanceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetInstanceProcAddr();
        InstancePointers pNew = default;

        fixed (byte* pName = "vkEnumeratePhysicalDevices"u8) {
            pNew.EnumeratePhysicalDevices = (delegate* unmanaged[Cdecl]<nint, ref uint, nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceProperties"u8) {
            pNew.GetPhysicalDeviceProperties = (delegate* unmanaged[Cdecl]<nint, nint, void>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceQueueFamilyProperties"u8) {
            pNew.GetPhysicalDeviceQueueFamilyProperties = (delegate* unmanaged[Cdecl]<nint, ref uint, nint, void>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceSurfaceSupportKHR"u8) {
            pNew.GetPhysicalDeviceSurfaceSupportKhr = (delegate* unmanaged[Cdecl]<nint, uint, nint, out uint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceSurfaceCapabilitiesKHR"u8) {
            pNew.GetPhysicalDeviceSurfaceCapabilitiesKhr = (delegate* unmanaged[Cdecl]<nint, nint, out VkSurfaceCapabilitiesKhr, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceSurfaceFormatsKHR"u8) {
            pNew.GetPhysicalDeviceSurfaceFormatsKhr = (delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceSurfacePresentModesKHR"u8) {
            pNew.GetPhysicalDeviceSurfacePresentModesKhr = (delegate* unmanaged[Cdecl]<nint, nint, ref uint, nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceFeatures"u8) {
            pNew.GetPhysicalDeviceFeatures = (delegate* unmanaged[Cdecl]<nint, nint, void>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceFeatures2"u8) {
            pNew.GetPhysicalDeviceFeatures2 = (delegate* unmanaged[Cdecl]<nint, nint, void>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPhysicalDeviceProperties2"u8) {
            pNew.GetPhysicalDeviceProperties2 = (delegate* unmanaged[Cdecl]<nint, nint, void>)getAddr(
                instanceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkEnumerateDeviceExtensionProperties"u8) {
            pNew.EnumerateDeviceExtensionProperties = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, VkResult>)getAddr(
                instanceHandle,
                pName
            );
        }
        m_pointers[instanceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is not null) {
                return m_getInstanceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");

            m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getInstanceProcAddr;
        }
    }
}
