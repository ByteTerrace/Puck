using System.Globalization;
using System.Text;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// What a GPU device's memory is, as its backend reported it when the device was created: whether the device shares
/// the host's memory coherently, how much device-local memory it has, its largest device-local heap, and how much
/// device-local memory the host can write through a coherent mapping. Unlike <see cref="GpuDeviceIdentity"/>, which is
/// recorded and never branched on, the profile is what <see cref="GpuResidency.Select"/> chooses a region's policy
/// from, and it names no platform, product or driver. The default value reports nothing, and a profile that reports
/// nothing selects the staged copy.
/// </summary>
/// <param name="CoherentUnifiedMemory">Whether the device's memory is the host's memory and host writes reach the device
/// without a copy or a flush: Direct3D 12's <c>UMA</c> with <c>CacheCoherentUMA</c>, or a Vulkan integrated or CPU
/// device with a device-local memory type that is host-visible and host-coherent.</param>
/// <param name="DeviceLocalBytes">The device-local memory in bytes: every device-local heap on Vulkan; the dedicated
/// video memory on a discrete Direct3D 12 adapter, and the dedicated and shared memory together on a unified one.</param>
/// <param name="LargestDeviceLocalHeapBytes">The largest device-local heap in bytes; Direct3D 12 reports one pool, so
/// there it equals <paramref name="DeviceLocalBytes"/>.</param>
/// <param name="HostVisibleDeviceLocalBytes">The largest device-local heap the host can write through a host-visible,
/// coherent mapping, in bytes: all of it on unified memory, the aperture on a discrete adapter that exposes one, and
/// zero when there is none.</param>
public readonly record struct GpuMemoryProfile(
    bool CoherentUnifiedMemory,
    ulong DeviceLocalBytes,
    ulong LargestDeviceLocalHeapBytes,
    ulong HostVisibleDeviceLocalBytes
) {
    // VkMemoryPropertyFlagBits, VkMemoryHeapFlagBits and VkPhysicalDeviceType values (vulkan_core.h).
    private const uint VulkanCpuDevice = 4U;
    private const uint VulkanHeapDeviceLocal = 0x1U;
    private const uint VulkanHostWritableDeviceLocal = VulkanMemoryDeviceLocal | VulkanMemoryHostCoherent | VulkanMemoryHostVisible;
    private const uint VulkanIntegratedDevice = 1U;
    private const uint VulkanMemoryDeviceLocal = 0x1U;
    private const uint VulkanMemoryHostCoherent = 0x4U;
    private const uint VulkanMemoryHostVisible = 0x2U;

    /// <summary>Gets whether any device-local memory is host-visible and coherent.</summary>
    public bool IsDeviceLocalHostVisible => (HostVisibleDeviceLocalBytes > 0UL);

    /// <summary>Fills a profile from what Direct3D 12 and DXGI report: <c>D3D12_FEATURE_DATA_ARCHITECTURE</c>'s
    /// <c>UMA</c> and <c>CacheCoherentUMA</c>, <c>DXGI_ADAPTER_DESC1</c>'s dedicated video and shared system memory, and
    /// <c>D3D12_FEATURE_DATA_D3D12_OPTIONS16</c>'s <c>GPUUploadHeapSupported</c>. Unified memory is one pool holding the
    /// dedicated and shared memory, all of it host-writable; a discrete adapter's pool is its dedicated memory, which
    /// the host can write only through GPU upload heaps.</summary>
    /// <param name="unifiedMemory">The architecture's <c>UMA</c>.</param>
    /// <param name="cacheCoherentUnifiedMemory">The architecture's <c>CacheCoherentUMA</c>.</param>
    /// <param name="dedicatedVideoMemory">The adapter's <c>DedicatedVideoMemory</c> in bytes.</param>
    /// <param name="sharedSystemMemory">The adapter's <c>SharedSystemMemory</c> in bytes.</param>
    /// <param name="gpuUploadHeapSupported">Options 16's <c>GPUUploadHeapSupported</c>; <see langword="false"/> when
    /// the runtime does not answer that query.</param>
    /// <returns>The profile.</returns>
    public static GpuMemoryProfile FromDirectX(bool unifiedMemory, bool cacheCoherentUnifiedMemory, ulong dedicatedVideoMemory, ulong sharedSystemMemory, bool gpuUploadHeapSupported) {
        if (unifiedMemory) {
            var pool = checked((dedicatedVideoMemory + sharedSystemMemory));

            return new GpuMemoryProfile(
                CoherentUnifiedMemory: cacheCoherentUnifiedMemory,
                DeviceLocalBytes: pool,
                HostVisibleDeviceLocalBytes: pool,
                LargestDeviceLocalHeapBytes: pool
            );
        }

        return new GpuMemoryProfile(
            CoherentUnifiedMemory: false,
            DeviceLocalBytes: dedicatedVideoMemory,
            HostVisibleDeviceLocalBytes: (gpuUploadHeapSupported
                ? dedicatedVideoMemory
                : 0UL
            ),
            LargestDeviceLocalHeapBytes: dedicatedVideoMemory
        );
    }
    /// <summary>Fills a profile from what Vulkan reports: <c>VkPhysicalDeviceProperties.deviceType</c> and
    /// <c>vkGetPhysicalDeviceMemoryProperties</c>'s memory types and heaps, in their native layout. A device-local heap
    /// counts toward the device-local total and the largest heap; a heap some memory type reaches as device-local,
    /// host-visible and host-coherent counts toward the host-writable device-local memory; and an integrated or CPU
    /// device with such a type is coherent unified memory.</summary>
    /// <param name="deviceType">The <c>VkPhysicalDeviceType</c>.</param>
    /// <param name="memoryTypes">The valid memory types as <c>{ propertyFlags, heapIndex }</c> pairs, two
    /// <see langword="uint"/>s per type (<c>VkMemoryType</c>).</param>
    /// <param name="memoryHeaps">The valid memory heaps as <c>{ size, flags }</c> pairs, two <see langword="ulong"/>s per
    /// heap (<c>VkMemoryHeap</c>, whose 32-bit flags are padded to eight bytes).</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ArgumentException"><paramref name="memoryTypes"/> or <paramref name="memoryHeaps"/> holds an odd
    /// count, or a memory type names a heap past <paramref name="memoryHeaps"/>.</exception>
    public static GpuMemoryProfile FromVulkan(uint deviceType, ReadOnlySpan<uint> memoryTypes, ReadOnlySpan<ulong> memoryHeaps) {
        if (
            ((memoryTypes.Length % 2) != 0) ||
            ((memoryHeaps.Length % 2) != 0)
        ) {
            throw new ArgumentException(message: "Vulkan memory types are {propertyFlags, heapIndex} pairs and heaps {size, flags} pairs; an odd count is not a native layout.");
        }

        var heapCount = (memoryHeaps.Length / 2);
        var deviceLocal = 0UL;
        var largest = 0UL;
        var hostWritable = 0UL;

        for (var heap = 0; (heap < heapCount); heap++) {
            if (!IsDeviceLocalHeap(heap: heap, memoryHeaps: memoryHeaps)) {
                continue;
            }

            var size = memoryHeaps[(heap * 2)];

            deviceLocal = checked((deviceLocal + size));
            largest = Math.Max(
                val1: largest,
                val2: size
            );
        }

        for (var type = 0; (type < memoryTypes.Length); type += 2) {
            var heap = memoryTypes[(type + 1)];

            if (heap >= ((uint)heapCount)) {
                throw new ArgumentException(message: $"Vulkan memory type {(type / 2)} names heap {heap}, past the {heapCount} reported.");
            }

            if (
                ((memoryTypes[type] & VulkanHostWritableDeviceLocal) == VulkanHostWritableDeviceLocal) &&
                IsDeviceLocalHeap(heap: ((int)heap), memoryHeaps: memoryHeaps)
            ) {
                hostWritable = Math.Max(
                    val1: hostWritable,
                    val2: memoryHeaps[((int)(heap * 2U))]
                );
            }
        }

        return new GpuMemoryProfile(
            CoherentUnifiedMemory: (
                ((deviceType == VulkanIntegratedDevice) || (deviceType == VulkanCpuDevice)) &&
                (hostWritable > 0UL)
            ),
            DeviceLocalBytes: deviceLocal,
            HostVisibleDeviceLocalBytes: hostWritable,
            LargestDeviceLocalHeapBytes: largest
        );
    }

    // Whether heap's VkMemoryHeapFlags (the low half of its second ulong) carry VK_MEMORY_HEAP_DEVICE_LOCAL_BIT.
    private static bool IsDeviceLocalHeap(int heap, ReadOnlySpan<ulong> memoryHeaps) =>
        ((((uint)memoryHeaps[((heap * 2) + 1)]) & VulkanHeapDeviceLocal) != 0U);

    /// <summary>Appends the profile as <c>key=value</c> fields on one line, every field present:
    /// <c> unified.coherent=no device-local=… device-local.largest-heap=… device-local.host-visible=…</c>, sizes in
    /// bytes.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public StringBuilder AppendFields(StringBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" unified.coherent={(CoherentUnifiedMemory ? "yes" : "no")} device-local={DeviceLocalBytes} device-local.largest-heap={LargestDeviceLocalHeapBytes} device-local.host-visible={HostVisibleDeviceLocalBytes}"
        );
    }
}
