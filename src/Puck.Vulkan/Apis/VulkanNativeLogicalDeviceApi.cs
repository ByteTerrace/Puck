using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanLogicalDeviceApi"/>, marshaling to the device-creation,
/// queue-retrieval, and wait-idle entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeLogicalDeviceApi : IVulkanLogicalDeviceApi {
    // A single over-sized, zeroed block per chained struct. We only enable the FIRST VkBool32 (the primary
    // feature) and require every trailing flag to read VK_FALSE; the driver reads exactly sizeof(struct) bytes
    // keyed off sType, so over-allocating is harmless but UNDER-allocating lets it read uninitialized memory
    // past the block, past which a too-small block
    // reads the adjacent block's sType as a bogus VkBool32. 256 bytes comfortably exceeds any
    // current Vulkan feature struct (even the aggregate VkPhysicalDeviceVulkan1xFeatures).
    private const int FeatureStructureByteSize = 256;
    // VkPhysicalDeviceFeatures is 55 consecutive VkBool32 fields.
    private const int PhysicalDeviceFeatureCount = 55;
    private const uint VkStructureTypeDeviceCreateInfo = 3;
    private const uint VkStructureTypeDeviceQueueCreateInfo = 2;
    // Values verified against the Vulkan SDK 1.4.350 header (vulkan_core.h).
    private const uint VkStructureTypePhysicalDeviceFeatures2 = 1000059000;

    private readonly IAllocator m_allocator;
    private readonly VulkanProcResolver m_procedures;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeLogicalDeviceApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <param name="procedures">The resolver the device's command table is resolved and counted through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> or <paramref name="procedures"/> is
    /// <see langword="null"/>.</exception>
    public VulkanNativeLogicalDeviceApi(IAllocator allocator, VulkanProcResolver procedures) {
        ArgumentNullException.ThrowIfNull(argument: allocator);
        ArgumentNullException.ThrowIfNull(argument: procedures);

        m_allocator = allocator;
        m_procedures = procedures;
    }

    // Every chained VkPhysicalDevice*Features struct shares the layout
    // { uint sType; nint pNext; VkBool32 flags[N]; ... } — sType at 0, pNext one pointer
    // in, the first feature flag two pointers in, the whole thing pointer-aligned.
    private static readonly int FeatureStructurePNextOffset = IntPtr.Size;
    private static readonly int FeatureStructureFlagOffset = (IntPtr.Size * 2);

    /// <inheritdoc/>
    public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Instance,
            paramName: nameof(request)
        );

        device = null;

        var createDevice = request.Instance.CreateDevice;

        var queues = request.Queues;
        var queuePriorities = new float[queues.Count];
        var queueInfos = new VkDeviceQueueCreateInfo[queues.Count];
        nint queuePrioritiesPointer = 0;
        nint queueInfosPointer = 0;
        Utf8StringArray? extensionNames = null;

        var featureIndices = request.EnabledFeatureIndices;
        var featureStructureTypes = request.EnabledFeatureStructureTypes;
        var hasFeatureChain = (featureStructureTypes.Count > 0);

        // Only allocated for the no-chain path; the chain path writes base features straight
        // into the VkPhysicalDeviceFeatures2.Features block instead.
        var enabledFeaturesBuffer = nint.Zero;
        var featureBlocks = new nint[(hasFeatureChain
            ? featureStructureTypes.Count
            : 0)];

        try {
            // One priority per queue, each queue's create info pointing at its own element.
            for (var index = 0; (index < queues.Count); index++) {
                queuePriorities[index] = queues[index].Priority;
            }

            queuePrioritiesPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: queuePriorities
            );

            for (var index = 0; (index < queues.Count); index++) {
                queueInfos[index] = new VkDeviceQueueCreateInfo {
                    PQueuePriorities = (queuePrioritiesPointer + (index * sizeof(float))),
                    QueueCount = 1,
                    QueueFamilyIndex = queues[index].FamilyIndex,
                    SType = VkStructureTypeDeviceQueueCreateInfo,
                };
            }

            queueInfosPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: queueInfos
            );
            extensionNames = Utf8StringArray.Create(
                allocator: m_allocator,
                values: request.ExtensionNames
            );

            var createInfo = new VkDeviceCreateInfo {
                EnabledExtensionCount = ((uint)extensionNames.Count),
                PQueueCreateInfos = queueInfosPointer,
                PpEnabledExtensionNames = extensionNames.Pointer,
                QueueCreateInfoCount = ((uint)queueInfos.Length),
                SType = VkStructureTypeDeviceCreateInfo,
            };

            // VkPhysicalDeviceFeatures2 is a stack local; vkCreateDevice consumes the whole
            // chain synchronously below, so its address (and the unmanaged blocks linked off
            // it) stay valid for the call.
            var features2 = new VkPhysicalDeviceFeatures2 {
                SType = VkStructureTypePhysicalDeviceFeatures2,
            };

            if (hasFeatureChain) {
                // Build the pNext chain generically: one { sType, pNext, VkBool32 = 1 } block
                // per requested feature struct. Order is immaterial to vkCreateDevice.
                nint chainHead = 0;

                for (var index = 0; (index < featureStructureTypes.Count); index++) {
                    var block = m_allocator.Alloc(size: FeatureStructureByteSize);

                    featureBlocks[index] = block;
                    new Span<byte>(
                        length: FeatureStructureByteSize,
                        pointer: ((void*)block)
                    ).Clear();
                    Marshal.WriteInt32(
                        ofs: 0,
                        ptr: block,
                        val: unchecked((int)featureStructureTypes[index])
                    );
                    Marshal.WriteIntPtr(
                        ofs: FeatureStructurePNextOffset,
                        ptr: block,
                        val: chainHead
                    );
                    Marshal.WriteInt32(
                        ofs: FeatureStructureFlagOffset,
                        ptr: block,
                        val: 1
                    );
                    chainHead = block;
                }

                // The spec requires pEnabledFeatures to be null when a Features2 chain is
                // used; the base feature flags live in Features2.Features instead.
                for (var index = 0; (index < featureIndices.Count); index++) {
                    features2.Features[((int)featureIndices[index])] = 1u;
                }

                features2.PNext = chainHead;
                createInfo.PNext = ((nint)(&features2));
            } else if (featureIndices.Count > 0) {
                enabledFeaturesBuffer = m_allocator.Alloc(size: (PhysicalDeviceFeatureCount * sizeof(uint)));
                new Span<byte>(
                    length: (PhysicalDeviceFeatureCount * sizeof(uint)),
                    pointer: ((void*)enabledFeaturesBuffer)
                ).Clear();
                for (var index = 0; (index < featureIndices.Count); index++) {
                    Marshal.WriteInt32(
                        ofs: (((int)featureIndices[index]) * sizeof(uint)),
                        ptr: enabledFeaturesBuffer,
                        val: 1
                    );
                }

                createInfo.PEnabledFeatures = enabledFeaturesBuffer;
            }

            var result = createDevice(
                request.PhysicalDevice.Handle,
                in createInfo,
                0,
                out var deviceHandle
            );

            if (
                (VkResult.Success == result) &&
                (0 != deviceHandle)
            ) {
                try {
                    device = new VulkanDeviceCommands(
                        deviceHandle: deviceHandle,
                        procedures: m_procedures
                    );
                } catch {
                    DestroyUnresolvedDevice(deviceHandle: deviceHandle);

                    throw;
                }
            }

            return result;
        } finally {
            m_allocator.Free(ptr: queueInfosPointer);
            m_allocator.Free(ptr: queuePrioritiesPointer);
            extensionNames?.Dispose();
            foreach (var block in featureBlocks) {
                if (0 != block) {
                    m_allocator.Free(ptr: block);
                }
            }

            if (0 != enabledFeaturesBuffer) {
                m_allocator.Free(ptr: enabledFeaturesBuffer);
            }
        }
    }
    /// <inheritdoc/>
    public void DestroyDevice(VulkanDeviceCommands device) {
        if (device is null) {
            return;
        }

        device.DestroyDevice(
            device.Handle,
            0
        );
        device.Dispose();
    }
    /// <inheritdoc/>
    public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) {
        ArgumentNullException.ThrowIfNull(argument: device);

        device.GetDeviceQueue(
            device.Handle,
            queueFamilyIndex,
            queueIndex,
            out var queueHandle
        );
        return queueHandle;
    }
    /// <inheritdoc/>
    public VkResult WaitIdle(VulkanDeviceCommands device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return device.DeviceWaitIdle(device.Handle);
    }

    // A device whose command table could not be built is still a live VkDevice; destroy it before the failure
    // propagates, resolving vkDestroyDevice alone because no table exists to hold it.
    private unsafe void DestroyUnresolvedDevice(nint deviceHandle) {
        var destroyDevice = ((delegate* unmanaged[Cdecl]<nint, nint, void>)m_procedures.ResolveOptionalDeviceProc(
            deviceHandle: deviceHandle,
            functionName: "vkDestroyDevice"u8
        ));

        if (null != destroyDevice) {
            destroyDevice(
                deviceHandle,
                0
            );
        }
    }
}
